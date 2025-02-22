﻿using BrewLib.UserInterface;
using BrewLib.Util;
using StorybrewEditor.Util;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tiny;
using Tiny.Formats.Json;

namespace StorybrewEditor.ScreenLayers
{
    public class StartMenu : UiScreenLayer
    {
        private LinearLayout mainLayout;
        private Button newProjectButton;
        private Button openProjectButton;
        private Button preferencesButton;
        private Button closeButton;

        private LinearLayout bottomRightLayout;
        private Button discordButton;
        private Button wikiButton;

        private LinearLayout bottomLayout;
        private Button updateButton;
        private Label versionLabel;

        public override void Load()
        {
            base.Load();

            WidgetManager.Root.StyleName = "panel";
            WidgetManager.Root.Add(mainLayout = new LinearLayout(WidgetManager)
            {
                AnchorTarget = WidgetManager.Root,
                AnchorFrom = BoxAlignment.Centre,
                AnchorTo = BoxAlignment.Centre,
                Padding = new FourSide(16),
                FitChildren = true,
                Children = new Widget[]
                {
                    newProjectButton = new Button(WidgetManager)
                    {
                        Text = "New project",
                        AnchorFrom = BoxAlignment.Centre,
                    },
                    openProjectButton = new Button(WidgetManager)
                    {
                        Text = "Open project",
                        AnchorFrom = BoxAlignment.Centre,
                    },
                    preferencesButton = new Button(WidgetManager)
                    {
                        Text = "Preferences",
                        AnchorFrom = BoxAlignment.Centre,
                        Disabled = true,
                    },
                    closeButton = new Button(WidgetManager)
                    {
                        Text = "Close",
                        AnchorFrom = BoxAlignment.Centre,
                    },
                },
            });

            WidgetManager.Root.Add(bottomRightLayout = new LinearLayout(WidgetManager)
            {
                AnchorTarget = WidgetManager.Root,
                AnchorFrom = BoxAlignment.BottomRight,
                AnchorTo = BoxAlignment.BottomRight,
                Padding = new FourSide(16),
                Horizontal = true,
                Fill = true,
                Children = new Widget[]
                {
                    discordButton = new Button(WidgetManager)
                    {
                        StyleName = "small",
                        Text = "Join Discord",
                        AnchorFrom = BoxAlignment.Centre,
                    },
                    wikiButton = new Button(WidgetManager)
                    {
                        StyleName = "small",
                        Text = "Wiki",
                        AnchorFrom = BoxAlignment.Centre,
                    },
                },
            });

            var text = new StringBuilder($"{Program.Name} {ReflectionUtil.GetVersionInfo()}");
            var file = new FileInfo("Coosu.Storyboard.Storybrew.dll");
            if (file.Exists)
            {
                var fvi = FileVersionInfo.GetVersionInfo(file.FullName);
                text.Append($" (Coosu {fvi.ProductVersion})");
            }
            else
            {
                text.Append($" (Coosu MISSING)");
            }

            text.Append("\r\nCoosu/storybrew (forked from Damnae/storybrew)");
            WidgetManager.Root.Add(bottomLayout = new LinearLayout(WidgetManager)
            {
                AnchorTarget = WidgetManager.Root,
                AnchorFrom = BoxAlignment.Bottom,
                AnchorTo = BoxAlignment.Bottom,
                Padding = new FourSide(16),
                Children = new Widget[]
                {
                    updateButton = new Button(WidgetManager)
                    {
                        Text = "Checking for updates",
                        AnchorFrom = BoxAlignment.Centre,
                        StyleName = "small",
                        Disabled = true,
                    },
                    versionLabel = new Label(WidgetManager)
                    {
                        StyleName = "small",
                        Text = text.ToString(),
                        AnchorFrom = BoxAlignment.Centre,
                    },
                },
            });

            newProjectButton.OnClick += (sender, e) => Manager.Add(new NewProjectMenu());
            openProjectButton.OnClick += (sender, e) => Manager.ShowOpenProject();
            wikiButton.OnClick += (sender, e) => Process.Start($"https://github.com/{Program.Repository}/wiki");
            discordButton.OnClick += (sender, e) => Process.Start(Program.DiscordUrl);
            closeButton.OnClick += (sender, e) => Exit();
            checkLatestVersion();
        }

        public override void Resize(int width, int height)
        {
            base.Resize(width, height);
            mainLayout.Pack(300);
            bottomLayout.Pack(600);
            bottomRightLayout.Pack((1024 - bottomLayout.Width) / 2);
        }

        private void checkLatestVersion()
        {
            NetHelper.Request($"https://api.github.com/repos/{Program.Repository}/releases?per_page=10&page=1", "cache/net/releases", 15 * 60,
                (response, exception) =>
                {
                    if (IsDisposed) return;
                    if (exception != null)
                    {
                        handleLastestVersionException(exception);
                        return;
                    }
                    try
                    {
                        var hasLatest = false;
                        var latestVersion = Program.Version;
                        var description = "";
                        var downloadUrl = (string)null;

                        var releases = TinyToken.ReadString<JsonFormat>(response);
                        foreach (var release in releases.Values<TinyObject>())
                        {
                            var isDraft = release.Value<bool>("draft");
                            var isPrerelease = release.Value<bool>("prerelease");
                            if (isDraft || isPrerelease) continue;

                            var name = release.Value<string>("name");
                            var version = new Version(name);

                            if (!hasLatest)
                            {
                                hasLatest = true;
                                latestVersion = version;

                                foreach (var asset in release.Values<TinyObject>("assets"))
                                {
                                    var downloadName = asset.Value<string>("name");
                                    if (downloadName.EndsWith(".zip"))
                                    {
                                        downloadUrl = asset.Value<string>("browser_download_url");
                                        break;
                                    }
                                }
                            }

                            if (Program.Version < version || Program.Version >= latestVersion)
                            {
                                var publishedAt = release.Value<string>("published_at");
                                var publishDate = DateTime.ParseExact(publishedAt, @"yyyy-MM-dd\THH:mm:ss\Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                                var authorName = release.Value<string>("author", "login");

                                var body = release.Value<string>("body");
                                if (body.Contains("---")) body = body.Substring(0, body.IndexOf("---"));
                                body = body.Replace("\r\n", "\n").Trim(' ', '\n');
                                body = $"v{version} - {authorName}, {publishDate.ToTimeAgo()}\n{body}\n\n";

                                var newDescription = description + body;
                                if (description.Length > 0 && newDescription.Count(c => c == '\n') > 35)
                                    break;

                                description = newDescription;
                            }
                            else break;
                        }

                        if (Program.Version < latestVersion)
                        {
                            updateButton.Text = $"Version {latestVersion} available!";
                            updateButton.Tooltip = $"What's new:\n\n{description.TrimEnd('\n')}";
                            updateButton.OnClick += (sender, e) =>
                            {
                                if (downloadUrl != null && latestVersion >= new Version(1, 4))
                                    Manager.Add(new UpdateMenu(downloadUrl));
                                else Updater.OpenLastestReleasePage();
                            };
                            updateButton.StyleName = "";
                            updateButton.Disabled = false;
                        }
                        else
                        {
                            versionLabel.Tooltip = $"Recent changes:\n\n{description.TrimEnd('\n')}";
                            updateButton.Displayed = false;
                        }
                        bottomLayout.Pack(600);
                    }
                    catch (Exception e)
                    {
                        handleLastestVersionException(e);
                    }
                });
        }

        private void handleLastestVersionException(Exception exception)
        {
            Trace.WriteLine($"Error while retrieving latest release information: {exception.Message}");

            versionLabel.Text = $"Could not retrieve latest release information:\n{exception.Message}\n\n{versionLabel.Text}";

            updateButton.Text = "See latest release";
            updateButton.OnClick += (sender, e) => Updater.OpenLastestReleasePage();
            updateButton.Disabled = false;
            bottomLayout.Pack(600);
        }
    }
}
