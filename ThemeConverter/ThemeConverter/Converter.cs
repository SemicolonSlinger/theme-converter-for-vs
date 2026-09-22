// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

namespace ThemeConverter
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using Newtonsoft.Json.Linq;
    using ThemeConverter.ColorCompiler;

    public sealed class Converter
    {
        private static Guid DarkThemeId = new Guid("{1ded0138-47ce-435e-84ef-9ec1f439b749}");
        private static Guid LightThemeId = new Guid("{de3dbbcd-f642-433c-8353-8f1df4370aba}");

        private static Lazy<Dictionary<string, ColorKey[]>> ScopeMappings = new Lazy<Dictionary<string, ColorKey[]>>(ParseMapping.CreateScopeMapping());
        private static Lazy<Dictionary<string, string>> CategoryGuids = new Lazy<Dictionary<string, string>>(ParseMapping.CreateCategoryGuids());
        private static Lazy<Dictionary<string, string>> VSCTokenFallback = new Lazy<Dictionary<string, string>>(ParseMapping.CreateVSCTokenFallback());
        private static Lazy<Dictionary<string, (float, string)>> OverlayMappings = new Lazy<Dictionary<string, (float, string)>>(ParseMapping.CreateOverlayMapping());
        private static Lazy<JObject> ShellMappings = new Lazy<JObject>(() => JObject.Parse(File.ReadAllText("ShellMappings.json")));
        private static Lazy<JObject> SyntaxDefaults = new Lazy<JObject>(() => JObject.Parse(File.ReadAllText("SyntaxDefaults.json")));

        // Name-based theme ids live in this namespace, so re-converting a theme keeps its id.
        private static readonly Guid ThemeIdNamespace = new Guid("{b0f3b6f1-6c1b-4f7e-9a53-2f0c1a7e4d10}");

        // Fonts & Colors categories store COLORREFs, which drop alpha: translucent colours are
        // composited onto the first resolvable base before they are written.
        private static readonly Dictionary<string, string[]> FontAndColorBases = new Dictionary<string, string[]>
        {
            ["Text Editor Text Manager Items"] = new[] { "editor.background" },
            ["Text Editor Language Service Items"] = new[] { "editor.background" },
            ["Text Editor Text Marker Items"] = new[] { "editor.background" },
            ["Text Editor MEF Items"] = new[] { "editor.background" },
            ["Roslyn Text Editor MEF Items"] = new[] { "editor.background" },
            ["Cpp Text Editor MEF Items"] = new[] { "editor.background" },
            ["WebEditor"] = new[] { "editor.background" },
            ["Find Results"] = new[] { "editor.background" },
            ["Folder Difference"] = new[] { "editor.background" },
            ["Performance Tips"] = new[] { "editor.background" },
            ["Locals"] = new[] { "editor.background" },
            ["Autos"] = new[] { "editor.background" },
            ["Watch"] = new[] { "editor.background" },
            ["CodeSense"] = new[] { "editor.background" },
            ["Output Window"] = new[] { "panel.background", "editor.background" },
            ["Immediate Window"] = new[] { "panel.background", "editor.background" },
            ["Command Window"] = new[] { "panel.background", "editor.background" },
            ["Package Manager Console"] = new[] { "panel.background", "editor.background" },
            ["Editor Tooltip"] = new[] { "editorHoverWidget.background", "editorWidget.background", "editor.background" },
        };

        // Text/background pairs checked by the contrast audit, as "Category&Name" of the emitted theme.
        private static readonly (string Text, string Background)[] AuditPairs =
        {
            ("Shell&TextFillPrimary", "Shell&SolidBackgroundFillBase"),
            ("Shell&TextFillPrimary", "Shell&SolidBackgroundFillSecondary"),
            ("Shell&TextFillPrimary", "Shell&SolidBackgroundFillTertiary"),
            ("Shell&TextFillPrimary", "Shell&SolidBackgroundFillQuaternary"),
            ("Shell&TextFillPrimary", "Shell&SurfaceBackgroundFillDefault"),
            ("Shell&TextFillPrimary", "ShellInternal&EnvironmentBackground"),
            ("Shell&TextFillPrimary", "ShellInternal&EnvironmentHeader"),
            ("Shell&TextFillSecondary", "Shell&SolidBackgroundFillTertiary"),
            ("Shell&TextFillSecondary", "Shell&SolidBackgroundFillQuaternary"),
            ("Shell&TextFillSecondary", "ShellInternal&EnvironmentBackground"),
            ("Shell&TextFillTertiary", "Shell&SolidBackgroundFillTertiary"),
            ("Shell&AccentTextFillPrimary", "Shell&SolidBackgroundFillTertiary"),
            ("Shell&TextOnAccentFillPrimary", "Shell&AccentFillDefault"),
            ("Shell&HyperlinkFillPrimary", "Shell&SolidBackgroundFillTertiary"),
            ("ShellInternal&EnvironmentBodyText", "ShellInternal&EnvironmentBody"),
            ("ShellInternal&StatusBarTextFillRest", "ShellInternal&StatusBarBackgroundFillRest"),
            ("ShellInternal&StatusBarTextFillDebugging", "ShellInternal&StatusBarBackgroundFillDebugging"),
            ("ShellInternal&StatusBarTextFillBuilding", "ShellInternal&StatusBarBackgroundFillBuilding"),
            ("ShellInternal&StatusBarTextFillSolutionLoading", "ShellInternal&StatusBarBackgroundFillSolutionLoading"),
            ("EditorOverride&PopupText", "EditorOverride&PopupBackground"),
            ("EditorOverride&PopupSubtleText", "EditorOverride&PopupBackground"),
            ("EditorOverride&PopupHyperlink", "EditorOverride&PopupBackground"),
            ("EditorOverride&PopupSelectedText", "EditorOverride&PopupSelectedBackground"),
            // The integrated terminal paints ToolWindowText on the redirected ToolWindowBackground.
            ("Environment&ToolWindowText", "Shell&SolidBackgroundFillTertiary"),
        };

        private const double MinimumContrast = 3.0;

        /// <summary>
        /// Convert the theme file and patch the pkgdef to the target VS if specified.
        /// </summary>
        /// <param name="themeJsonFilePath">The VS Code theme json file path.</param>
        /// <param name="pkgdefOutputPath">Output folder path to write the .pkgdef file to.</param>
        /// <param name="themeId">Theme id to register; derived from the theme name when null.</param>
        /// <returns>
        /// Full path to the theme .pkgdef file created in the <paramref name="pkgdefOutputPath"/> folder.
        /// A contrast report, &lt;theme&gt;.audit.txt, is written beside it.
        /// </returns>
        public static string ConvertFile(string themeJsonFilePath, string pkgdefOutputPath, Guid? themeId = null)
        {
            string themeName = Path.GetFileNameWithoutExtension(themeJsonFilePath);

            // Parse VS Code theme file and uncomment the code.

            var lines = File.ReadAllLines(themeJsonFilePath);

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().StartsWith("//"))
                {
                    lines[i] = lines[i].Remove(lines[i].IndexOf("//"), 2);

                    if (!lines[i - 1].EndsWith(',') && !lines[i - 1].EndsWith('{'))
                    {
                        lines[i - 1] = lines[i - 1] + ",";
                    }
                }
            }

            string text = string.Empty;
            foreach (string str in lines)
            {
                text += str;
            }

            var jobject = JObject.Parse(text);
            var theme = jobject.ToObject<ThemeFileContract>();

            if (theme == null)
                throw new Exception("Failed to get theme object.");

            Guid fallbackId = ResolveFallbackId(theme.Type);

            // Group colors by category.
            var colorCategories = GroupColorsByCategory(theme);
            AddShellColors(theme, colorCategories);
            AddSyntaxDefaults(theme, colorCategories);
            FlattenFontAndColorCategories(theme, colorCategories);

            // Compile VS theme.
            string tempPkgdefFilePath = CompileVsTheme(themeName, themeId ?? ThemeIdFromName(themeName), fallbackId, colorCategories);
            try
            {
                // Copy pkgdef to specified folder
                Directory.CreateDirectory(pkgdefOutputPath);

                string destPkgdefFilePath = Path.Combine(pkgdefOutputPath, $"{themeName}.pkgdef");
                File.Copy(tempPkgdefFilePath, destPkgdefFilePath, overwrite: true);

                File.WriteAllLines(Path.Combine(pkgdefOutputPath, $"{themeName}.audit.txt"), AuditContrast(colorCategories));

                return destPkgdefFilePath;
            }
            finally
            {
                // Delete temporary file.
                File.Delete(tempPkgdefFilePath);
            }
        }

        public static void ValidateDataFiles(Action<string> reportFunc)
        {
            ParseMapping.CheckDuplicateMapping(reportFunc);
        }

        /// <summary>
        /// Maps a VS Code theme type (the theme file's "type", or a package's "uiTheme") to the
        /// Visual Studio theme that supplies every colour the converted theme leaves unset.
        /// </summary>
        public static Guid ResolveFallbackId(string? themeType)
        {
            switch (themeType?.ToLowerInvariant())
            {
                case null:
                case "light":
                case "vs":
                case "hclight":
                case "hc-light":
                    return LightThemeId;
                case "dark":
                case "vs-dark":
                case "hcdark":
                case "hc-black":
                    return DarkThemeId;
                default:
                    throw new ApplicationException($"Unrecognised theme type '{themeType}'.");
            }
        }

        /// <summary>
        /// Name-based (version 5) UUID for a theme, stable across conversions.
        /// </summary>
        public static Guid ThemeIdFromName(string themeName)
        {
            byte[] namespaceBytes = ThemeIdNamespace.ToByteArray();
            SwapGuidByteOrder(namespaceBytes);

            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(themeName);
            byte[] input = new byte[namespaceBytes.Length + nameBytes.Length];
            namespaceBytes.CopyTo(input, 0);
            nameBytes.CopyTo(input, namespaceBytes.Length);

            byte[] hash;
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                hash = sha1.ComputeHash(input);
            }

            byte[] id = new byte[16];
            Array.Copy(hash, id, 16);
            id[6] = (byte)((id[6] & 0x0F) | 0x50);
            id[8] = (byte)((id[8] & 0x3F) | 0x80);
            SwapGuidByteOrder(id);
            return new Guid(id);
        }

        // Guid.ToByteArray is little-endian in its first three fields; RFC 4122 hashes network order.
        private static void SwapGuidByteOrder(byte[] guid)
        {
            Array.Reverse(guid, 0, 4);
            Array.Reverse(guid, 4, 2);
            Array.Reverse(guid, 6, 2);
        }

        #region Compile VS Theme

        /// <summary>
        /// Generate the pkgdef from the theme.
        /// </summary>
        /// <param name="themeName">The name of theme.</param>
        /// <param name="themeGuid">The id the theme is registered under.</param>
        /// <param name="fallbackId">The built-in theme supplying unset colours.</param>
        /// <param name="colorCategories">Colors grouped by category.</param>
        /// <returns>Path to the generated pkgdef</returns>
        private static string CompileVsTheme(
           string themeName,
           Guid themeGuid,
           Guid fallbackId,
           Dictionary<string, Dictionary<string, SettingsContract>> colorCategories)
        {
            using (TempFileCollection tempFileCollection = new TempFileCollection())
            {
                string tempThemeFile = tempFileCollection.AddExtension("vstheme");

                using (var writer = new StreamWriter(tempThemeFile))
                {
                    writer.WriteLine($"<Themes>");
                    writer.WriteLine($"    <Theme Name=\"{themeName}\" GUID=\"{themeGuid:B}\" FallbackId=\"{fallbackId:B}\">");

                    foreach (var category in colorCategories)
                    {
                        writer.WriteLine($"        <Category Name=\"{category.Key}\" GUID=\"{CategoryGuids.Value[category.Key]}\">");

                        foreach (var color in category.Value)
                        {
                            if (color.Value.Foreground is not null || color.Value.Background is not null)
                            {
                                WriteColor(writer, color.Key, color.Value.Foreground, color.Value.Background);
                            }
                        }

                        writer.WriteLine($"        </Category>");
                    }

                    writer.WriteLine($"    </Theme>");
                    writer.WriteLine($"</Themes>");

                }

                // Compile the pkgdef
                XmlFileReader reader = new XmlFileReader(tempThemeFile);
                ColorManager manager = reader.ColorManager;

                string tempPkgdef = tempFileCollection.AddExtension("pkgdef", keepFile: true);
                FileWriter.SaveColorManagerToFile(manager, tempPkgdef, true);

                return tempPkgdef;
            }
        }
        #endregion Compile VS Theme

        #region Translate VS Theme

        /// <summary>
        /// Group converted colors by category.
        /// </summary>
        /// <param name="theme">the theme contract.</param>
        /// <returns>Mapping from Category to Color Tokens</returns>
        private static Dictionary<string, Dictionary<string, SettingsContract>> GroupColorsByCategory(ThemeFileContract theme)
        {
            // category -> colorKeyName => color value 
            var colorCategories = new Dictionary<string, Dictionary<string, SettingsContract>>();
            // category -> colorKeyName -> assigned by VSC token
            var assignBy = new Dictionary<string, Dictionary<string, string>>();

            Dictionary<string, bool> keyUsed = new Dictionary<string, bool>();
            foreach (string key in ScopeMappings.Value.Keys)
            {
                keyUsed.Add(key, false);
            }

            // Add the editor colors
            if (theme.TokenColors != null)
            {
                foreach (var ruleContract in theme.TokenColors)
                {
                    foreach (var scopeName in ruleContract.ScopeNames)
                    {
                        string[] scopes = scopeName.Split(',');
                        foreach (var scopeRaw in scopes)
                        {
                            var scope = scopeRaw.Trim();
                            foreach (string key in ScopeMappings.Value.Keys)
                            {
                                if (key.StartsWith(scope) && scope != "")
                                {
                                    if (ScopeMappings.Value.TryGetValue(key, out var colorKeys))
                                    {
                                        keyUsed[key] = true;
                                        AssignEditorColors(colorKeys, scope, ruleContract, ref colorCategories, ref assignBy);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // for keys that were not used during hierarchical assigning, check if there's any fallback that we can use...
            foreach (string key in keyUsed.Keys)
            {
                if (!keyUsed[key])
                {
                    if (VSCTokenFallback.Value.TryGetValue(key, out var fallbackToken))
                    {
                        // if the fallback is foreground, assign it like a shell color
                        if (fallbackToken == "foreground" && theme.Colors.ContainsKey("foreground"))
                        {
                            if (ScopeMappings.Value.TryGetValue(key, out var colorKeys))
                            {
                                AssignShellColors(theme, theme.Colors["foreground"], colorKeys, ref colorCategories);
                            }
                        }

                        if (theme.TokenColors != null)
                        {
                            foreach (var ruleContract in theme.TokenColors)
                            {
                                foreach (var scopeName in ruleContract.ScopeNames)
                                {
                                    string[] scopes = scopeName.Split(',');
                                    foreach (var scopeRaw in scopes)
                                    {
                                        var scope = scopeRaw.Trim();

                                        if ((fallbackToken.StartsWith(scope) && scope != ""))
                                        {
                                            if (ScopeMappings.Value.TryGetValue(key, out var colorKeys))
                                            {
                                                AssignEditorColors(colorKeys, scope, ruleContract, ref colorCategories, ref assignBy);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Add the shell colors
            foreach (var color in theme.Colors)
            {
                if (ScopeMappings.Value.TryGetValue(color.Key.Trim(), out var colorKeyList))
                {
                    if (!TryGetColorValue(theme, color.Key, out string? colorValue))
                    {
                        continue;
                    }

                    // calculate the actual border color for editor overlay colors
                    if (OverlayMappings.Value.ContainsKey(color.Key) && TryGetColorValue(theme, OverlayMappings.Value[color.Key].Item2, out string? backgroundColor))
                    {
                        colorValue = GetCompoundColor(colorValue!, backgroundColor!, VSOpacity: OverlayMappings.Value[color.Key].Item1);
                    }

                    AssignShellColors(theme, colorValue!, colorKeyList, ref colorCategories);
                }
            }

            return colorCategories;
        }

        private static bool TryGetColorValue(ThemeFileContract theme, string token, out string? colorValue)
        {
            theme.Colors.TryGetValue(token, out colorValue);

            string key = token;

            while (colorValue == null)
            {
                if (VSCTokenFallback.Value.TryGetValue(key, out var fallbackToken))
                {
                    key = fallbackToken;
                    theme.Colors.TryGetValue(key, out colorValue);
                }
                else
                {
                    break;
                }
            }

            return colorValue != null;
        }

        /// <summary>
        /// First colour in <paramref name="keys"/> that resolves to something visible.
        /// </summary>
        private static bool TryGetFirstColor(ThemeFileContract theme, JToken? keys, out string color)
        {
            color = string.Empty;
            if (keys is not JArray array)
            {
                return false;
            }

            foreach (var key in array)
            {
                if (TryGetColorValue(theme, key.ToString(), out string? value) && ToArgb(value!).A != 0)
                {
                    color = value!;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Emits the Visual Studio 18 Fluent tokens described by ShellMappings.json. The shell draws
        /// its chrome from these, and redirects the legacy Environment surface keys to them.
        /// </summary>
        private static void AddShellColors(ThemeFileContract theme, Dictionary<string, Dictionary<string, SettingsContract>> colorCategories)
        {
            foreach (var mapping in ShellMappings.Value.Properties())
            {
                string[] target = mapping.Name.Split('&');
                if (target.Length != 2 || mapping.Value is not JObject spec)
                {
                    continue;
                }

                if (!TryGetFirstColor(theme, spec["from"], out string color))
                {
                    continue;
                }

                if (TryGetFirstColor(theme, spec["over"], out string baseColor))
                {
                    color = Composite(color, baseColor);
                }

                if (spec["mix"] is JObject mix)
                {
                    color = Mix(color, mix["with"]!.ToString(), mix["amount"]!.ToObject<double>());
                }

                if (spec["alpha"] is JToken alpha)
                {
                    var argb = ToArgb(color);
                    color = FromArgb((alpha.ToObject<int>(), argb.R, argb.G, argb.B));
                }

                if (!colorCategories.TryGetValue(target[0], out var colors))
                {
                    colors = new Dictionary<string, SettingsContract>();
                    colorCategories[target[0]] = colors;
                }

                colors[target[1]] = new SettingsContract { Background = color };
            }
        }

        /// <summary>
        /// Gives every syntax classification in SyntaxDefaults.json that the token mappings left unset
        /// the colour VS Code would show: its scope resolved against the theme, else editor.foreground.
        /// </summary>
        private static void AddSyntaxDefaults(ThemeFileContract theme, Dictionary<string, Dictionary<string, SettingsContract>> colorCategories)
        {
            if (!TryGetColorValue(theme, "editor.foreground", out string? editorForeground))
            {
                return;
            }

            foreach (var mapping in SyntaxDefaults.Value.Properties())
            {
                string[] target = mapping.Name.Split('&');
                if (target.Length != 2 || IsColorSet(colorCategories, target[0], target[1]))
                {
                    continue;
                }

                if (!colorCategories.TryGetValue(target[0], out var colors))
                {
                    colors = new Dictionary<string, SettingsContract>();
                    colorCategories[target[0]] = colors;
                }

                colors[target[1]] = new SettingsContract { Foreground = MatchScope(theme, mapping.Value.ToString()) ?? editorForeground };
            }
        }

        // Category names are aliases when they share a GUID (e.g. the "... MEF Items" categories).
        private static bool IsColorSet(Dictionary<string, Dictionary<string, SettingsContract>> colorCategories, string category, string name)
        {
            string guid = CategoryGuids.Value[category];
            foreach (var other in colorCategories)
            {
                if (string.Equals(CategoryGuids.Value[other.Key], guid, StringComparison.OrdinalIgnoreCase) &&
                    other.Value.TryGetValue(name, out var entry) && (entry.Foreground is not null || entry.Background is not null))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Foreground a TextMate theme gives a token with scope stack <paramref name="scopePath"/>
        /// (outermost first): the rule whose selector matches deepest wins, then the longest
        /// matching selector, then the one naming more ancestors, then the later rule.
        /// </summary>
        internal static string? MatchScope(ThemeFileContract theme, string scopePath)
        {
            if (theme.TokenColors is null)
            {
                return null;
            }

            string[] path = scopePath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            (int Depth, int Length, int Parts) best = (-1, -1, -1);
            string? color = null;

            static bool Matches(string selector, string scope) =>
                scope == selector || scope.StartsWith(selector + ".", StringComparison.Ordinal);

            foreach (var rule in theme.TokenColors)
            {
                if (rule.Settings?.Foreground is null)
                {
                    continue;
                }

                foreach (var scopeName in rule.ScopeNames)
                {
                    foreach (var selectorRaw in scopeName.Split(','))
                    {
                        string[] parts = selectorRaw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0)
                        {
                            continue;
                        }

                        // The last selector part must match the deepest possible element, and the
                        // earlier parts must match ancestors in order.
                        for (int depth = path.Length - 1; depth >= 0; depth--)
                        {
                            if (!Matches(parts[^1], path[depth]))
                            {
                                continue;
                            }

                            int next = depth - 1;
                            bool ancestorsMatch = true;
                            for (int p = parts.Length - 2; p >= 0 && ancestorsMatch; p--)
                            {
                                while (next >= 0 && !Matches(parts[p], path[next]))
                                {
                                    next--;
                                }

                                ancestorsMatch = next-- >= 0;
                            }

                            if (ancestorsMatch)
                            {
                                var rank = (depth, parts[^1].Length, parts.Length);
                                if (rank.CompareTo(best) >= 0)
                                {
                                    best = rank;
                                    color = rule.Settings.Foreground;
                                }

                                break;
                            }
                        }
                    }
                }
            }

            return color;
        }

        /// <summary>
        /// Composites translucent Fonts &amp; Colors entries onto what they are drawn over.
        /// </summary>
        private static void FlattenFontAndColorCategories(ThemeFileContract theme, Dictionary<string, Dictionary<string, SettingsContract>> colorCategories)
        {
            foreach (var category in colorCategories)
            {
                if (!FontAndColorBases.TryGetValue(category.Key, out var baseKeys) ||
                    !TryGetFirstColor(theme, new JArray(baseKeys), out string categoryBase))
                {
                    continue;
                }

                categoryBase = Composite(categoryBase, "#000000");

                foreach (var name in new List<string>(category.Value.Keys))
                {
                    var entry = category.Value[name];
                    string? background = entry.Background is null ? null : Composite(entry.Background, categoryBase);
                    string? foreground = entry.Foreground is null ? null : Composite(entry.Foreground, background ?? categoryBase);

                    // Entries can share one SettingsContract with a token rule, so replace rather than mutate.
                    category.Value[name] = new SettingsContract { Foreground = foreground, Background = background };
                }
            }
        }

        /// <summary>
        /// Lists emitted text/background pairs whose contrast falls below <see cref="MinimumContrast"/>,
        /// including pairs set within one Fonts &amp; Colors entry.
        /// </summary>
        private static List<string> AuditContrast(Dictionary<string, Dictionary<string, SettingsContract>> colorCategories)
        {
            var report = new List<string>();

            string? Lookup(string qualifiedName)
            {
                string[] parts = qualifiedName.Split('&');
                return colorCategories.TryGetValue(parts[0], out var colors) && colors.TryGetValue(parts[1], out var entry)
                    ? entry.Background ?? entry.Foreground
                    : null;
            }

            void Check(string text, string textName, string background, string backgroundName)
            {
                string opaqueBackground = Composite(background, "#000000");
                double ratio = ContrastRatio(Composite(text, opaqueBackground), opaqueBackground);
                if (ratio < MinimumContrast)
                {
                    report.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.00}:1  {1} {2} on {3} {4}",
                        ratio, textName, ReviseColor(text), backgroundName, ReviseColor(background)));
                }
            }

            foreach (var (textName, backgroundName) in AuditPairs)
            {
                string? text = Lookup(textName);
                string? background = Lookup(backgroundName);
                if (text is not null && background is not null)
                {
                    Check(text, textName, background, backgroundName);
                }
            }

            foreach (var category in colorCategories)
            {
                if (!FontAndColorBases.ContainsKey(category.Key))
                {
                    continue;
                }

                foreach (var entry in category.Value)
                {
                    if (entry.Value.Foreground is not null && entry.Value.Background is not null)
                    {
                        Check(entry.Value.Foreground, $"{category.Key}&{entry.Key}&Foreground",
                              entry.Value.Background, $"{category.Key}&{entry.Key}&Background");
                    }
                }
            }

            report.Sort(StringComparer.Ordinal);
            report.Insert(0, report.Count == 0
                ? "No text/background pair is below the minimum contrast."
                : string.Format(CultureInfo.InvariantCulture, "{0} pair(s) below {1}:1", report.Count, MinimumContrast));
            return report;
        }

        private static (int A, int R, int G, int B) ToArgb(string color)
        {
            string argb = ReviseColor(color);
            if (argb.Length != 8)
            {
                throw new ApplicationException($"Unrecognised colour '{color}'.");
            }

            int Channel(int index) => System.Convert.ToInt32(argb.Substring(index, 2), 16);
            return (Channel(0), Channel(2), Channel(4), Channel(6));
        }

        // VS Code notation (#RRGGBBAA), which the writer converts with ReviseColor.
        private static string FromArgb((int A, int R, int G, int B) c)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", c.R, c.G, c.B, c.A);
        }

        private static string Composite(string overlay, string baseColor)
        {
            var o = ToArgb(overlay);
            if (o.A == 255)
            {
                return FromArgb(o);
            }

            var b = ToArgb(baseColor);
            double a = o.A / 255.0;
            int Blend(int top, int bottom) => (int)Math.Round(a * top + (1 - a) * bottom);
            return FromArgb((255, Blend(o.R, b.R), Blend(o.G, b.G), Blend(o.B, b.B)));
        }

        private static string Mix(string color, string with, double amount)
        {
            var c = ToArgb(color);
            var w = ToArgb(with);
            int Blend(int from, int to) => (int)Math.Round((1 - amount) * from + amount * to);
            return FromArgb((c.A, Blend(c.R, w.R), Blend(c.G, w.G), Blend(c.B, w.B)));
        }

        // WCAG 2 contrast ratio of two opaque colours.
        private static double ContrastRatio(string first, string second)
        {
            static double Luminance(string color)
            {
                var c = ToArgb(color);
                static double Linear(int channel)
                {
                    double s = channel / 255.0;
                    return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
                }

                return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
            }

            double l1 = Luminance(first);
            double l2 = Luminance(second);
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }

        /// <summary>
        /// Compute what is the compound color of 2 overlayed colors with transparency
        /// </summary>
        /// <param name="VSOpacity">What is the opacity that VS will use when displaying this color</param>
        /// <param name="VSCOpacity">The opacity that VSC will apply to this token under special circumstances.</param>
        /// <returns>Color value for VS</returns>
        private static string GetCompoundColor(string overlayColor, string baseColor, float VSOpacity = 1, float VSCOpacity = 1)
        {
            overlayColor = ReviseColor(overlayColor);
            baseColor = ReviseColor(baseColor);
            float overlayA = (float)System.Convert.ToInt32(overlayColor.Substring(0, 2), 16) * VSCOpacity / 255;
            float overlayR = System.Convert.ToInt32(overlayColor.Substring(2, 2), 16);
            float overlayG = System.Convert.ToInt32(overlayColor.Substring(4, 2), 16);
            float overlayB = System.Convert.ToInt32(overlayColor.Substring(6, 2), 16);

            float baseA = (float)System.Convert.ToInt32(baseColor.Substring(0, 2), 16) / 255;
            float baseR = System.Convert.ToInt32(baseColor.Substring(2, 2), 16);
            float baseG = System.Convert.ToInt32(baseColor.Substring(4, 2), 16);
            float baseB = System.Convert.ToInt32(baseColor.Substring(6, 2), 16);

            float R = (overlayA / VSOpacity) * overlayR + (1 - overlayA / VSOpacity) * baseA * baseR;
            float G = (overlayA / VSOpacity) * overlayG + (1 - overlayA / VSOpacity) * baseA * baseG;
            float B = (overlayA / VSOpacity) * overlayB + (1 - overlayA / VSOpacity) * baseA * baseB;

            R = Math.Clamp(R, 0, 255);
            G = Math.Clamp(G, 0, 255);
            B = Math.Clamp(B, 0, 255);

            return $"{(int)R:X2}{(int)G:X2}{(int)B:X2}FF";
        }

        private static void AssignEditorColors(ColorKey[] colorKeys,
                                        string scope,
                                        RuleContract ruleContract,
                                        ref Dictionary<string, Dictionary<string, SettingsContract>> colorCategories,
                                        ref Dictionary<string, Dictionary<string, string>> assignBy)
        {
            foreach (var colorKey in colorKeys)
            {
                if (!colorCategories.TryGetValue(colorKey.CategoryName, out var rulesList))
                {
                    rulesList = new Dictionary<string, SettingsContract>();
                    colorCategories[colorKey.CategoryName] = rulesList;
                }

                if (!assignBy.TryGetValue(colorKey.CategoryName, out var assignList))
                {
                    assignList = new Dictionary<string, string>();
                    assignBy[colorKey.CategoryName] = assignList;
                }

                if (rulesList.ContainsKey(colorKey.KeyName))
                {
                    if (scope.StartsWith(assignList[colorKey.KeyName]) && ruleContract.Settings.Foreground != null)
                    {
                        rulesList[colorKey.KeyName] = ruleContract.Settings;
                        assignList[colorKey.KeyName] = scope;
                    }
                }
                else
                {
                    rulesList.Add(colorKey.KeyName, ruleContract.Settings);
                    assignList.Add(colorKey.KeyName, scope);
                }
            }
        }

        private static void AssignShellColors(ThemeFileContract theme, string colorValue, ColorKey[] colorKeys, ref Dictionary<string, Dictionary<string, SettingsContract>> colorCategories)
        {
            foreach (var colorKey in colorKeys)
            {
                if (colorKey.ForegroundOpacity is not null && colorKey.VSCBackground is not null)
                {
                    if (TryGetColorValue(theme, colorKey.VSCBackground, out string? backgroundColor))
                    {
                        colorValue = GetCompoundColor(colorValue, backgroundColor!, 1, colorKey.ForegroundOpacity.Value);
                    }
                }

                if (!colorCategories.TryGetValue(colorKey.CategoryName, out var rulesList))
                {
                    // token name to colors
                    rulesList = new Dictionary<string, SettingsContract>();
                    colorCategories[colorKey.CategoryName] = rulesList;
                }

                if (!rulesList.TryGetValue(colorKey.KeyName, out var colorSetting))
                {
                    colorSetting = new SettingsContract();
                    rulesList.Add(colorKey.KeyName, colorSetting);
                }

                if (colorKey.isBackground)
                {
                    colorSetting.Background = colorValue;
                }
                else
                {
                    colorSetting.Foreground = colorValue;
                }
            }
        }

        #endregion Translate VS Theme

        #region Write VS Theme

        private static void WriteColor(StreamWriter writer, string colorKeyName, string? foregroundColor, string? backgroundColor)
        {
            writer.WriteLine($"            <Color Name=\"{colorKeyName}\">");

            if (backgroundColor is not null)
            {
                writer.WriteLine($"                <Background Type=\"CT_RAW\" Source=\"{ReviseColor(backgroundColor)}\"/>");
            }

            if (foregroundColor is not null)
            {
                writer.WriteLine($"                <Foreground Type=\"CT_RAW\" Source=\"{ReviseColor(foregroundColor)}\"/>");
            }

            writer.WriteLine($"            </Color>");
        }

        private static string ReviseColor(string color)
        {
            var revisedColor = color.Trim('#');
            switch (revisedColor.Length)
            {
                case 3:
                    {
                        string r = revisedColor.Substring(0, 1);
                        string g = revisedColor.Substring(1, 1);
                        string b = revisedColor.Substring(2, 1);
                        revisedColor = string.Format("FF{0}{0}{1}{1}{2}{2}", r, g, b);
                        break;
                    }
                case 4:
                    {
                        string r = revisedColor.Substring(0, 1);
                        string g = revisedColor.Substring(1, 1);
                        string b = revisedColor.Substring(2, 1);
                        string a = revisedColor.Substring(3, 1);
                        revisedColor = string.Format("{0}{0}{1}{1}{2}{2}{3}{3}", a, r, g, b);
                        break;
                    }
                case 6:
                    {
                        revisedColor = $"FF{revisedColor}";
                        break;
                    }
                case 8:
                    {
                        // go from RRGGBBAA to AARRGGBB
                        revisedColor = string.Format("{0}{1}", revisedColor.Substring(6), revisedColor.Substring(0, 6));
                        break;
                    }
                default:
                    break;
            }
            return revisedColor;
        }

        #endregion Write VS Theme
    }

    internal sealed class ColorKey
    {
        public ColorKey(string categoryName, string keyName, string backgroundOrForeground, string? foregroundOpacity = null, string? vscBackground = null)
        {
            this.CategoryName = categoryName;
            this.KeyName = keyName;
            this.Aspect = backgroundOrForeground;

            if (backgroundOrForeground.Equals("Background", StringComparison.OrdinalIgnoreCase))
            {
                isBackground = true;
            }
            else
            {
                isBackground = false;
            }

            this.ForegroundOpacity = foregroundOpacity == null ? null : float.Parse(foregroundOpacity, CultureInfo.InvariantCulture.NumberFormat);
            this.VSCBackground = vscBackground;
        }

        public string CategoryName { get; }

        public string KeyName { get; }

        public string Aspect { get; }

        public bool isBackground { get; }

        public float? ForegroundOpacity { get; }

        public string? VSCBackground { get; }

        public override string ToString()
        {
            return this.CategoryName + "&" + this.KeyName + "&" + this.Aspect;
        }
    }
}
