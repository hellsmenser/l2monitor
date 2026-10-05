using L2Monitor.Core.Api;
using L2Monitor.Tray.Api;
using L2Monitor.Tray.Bootstrap;
using System.Net;
using System.Text;
using System.Text.Json;
using L2Monitor.Tray.Ui;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Xunit;

namespace L2Monitor.Tray.Tests.Ui;

public sealed class MainWindowTests
{
    [Fact]
    public void UpdateBanner_RemainsVisibleWithReleaseLinkWhenNewVersionIsAvailable()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var update = new ClientUpdateDto(
                State: "available",
                CurrentVersion: "1.0.1",
                LatestVersion: "1.0.2",
                IsUpdateAvailable: true,
                Required: true,
                ReleaseUrl: "https://github.com/hellsmenser/l2monitor/releases/tag/v1.0.2",
                CheckedAtUtc: DateTimeOffset.UtcNow);

            window.ApplyUpdateStatus(update);

            var banner = Assert.IsType<Border>(window.FindName("UpdateBannerBorder"));
            var text = Assert.IsType<TextBlock>(window.FindName("UpdateBannerText"));
            var button = Assert.IsType<Button>(window.FindName("OpenReleaseButton"));
            Assert.Equal(Visibility.Visible, banner.Visibility);
            Assert.Contains("1.0.2", text.Text, StringComparison.Ordinal);
            Assert.Contains("обязательное", text.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(button.IsEnabled);
            window.Close();
        });
    }
    [Fact]
    public void UpdateBanner_RemainsVisibleWhenBackendHasNotPublishedDownloadUrlYet()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var update = new ClientUpdateDto(
                State: "available",
                CurrentVersion: "1.0.1",
                LatestVersion: "1.0.2",
                IsUpdateAvailable: true,
                Required: false,
                ReleaseUrl: null,
                CheckedAtUtc: DateTimeOffset.UtcNow);

            window.ApplyUpdateStatus(update);

            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("UpdateBannerBorder")).Visibility);
            Assert.False(Assert.IsType<Button>(window.FindName("OpenReleaseButton")).IsEnabled);
            window.Close();
        });
    }
    [Fact]
    public void MainWindowScrollBarStyle_DoesNotForceVerticalBarsToFourteenPixelsTall()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var style = Assert.IsType<Style>(window.Resources[typeof(ScrollBar)]);

            Assert.DoesNotContain(style.Setters.OfType<Setter>(), setter => setter.Property == FrameworkElement.HeightProperty);
            Assert.DoesNotContain(style.Setters.OfType<Setter>(), setter => setter.Property == FrameworkElement.WidthProperty);

            var triggers = style.Triggers.OfType<Trigger>().ToArray();
            var horizontalTrigger = Assert.Single(triggers, trigger =>
                trigger.Property == ScrollBar.OrientationProperty
                && Equals(trigger.Value, Orientation.Horizontal));
            var verticalTrigger = Assert.Single(triggers, trigger =>
                trigger.Property == ScrollBar.OrientationProperty
                && Equals(trigger.Value, Orientation.Vertical));

            Assert.Contains(horizontalTrigger.Setters.OfType<Setter>(), setter =>
                setter.Property == FrameworkElement.HeightProperty
                && Equals(setter.Value, 14d));
            Assert.Contains(horizontalTrigger.Setters.OfType<Setter>(), setter =>
                setter.Property == Control.TemplateProperty);
            Assert.DoesNotContain(horizontalTrigger.Setters.OfType<Setter>(), setter => setter.Property == FrameworkElement.WidthProperty);

            Assert.Contains(verticalTrigger.Setters.OfType<Setter>(), setter =>
                setter.Property == FrameworkElement.WidthProperty
                && Equals(setter.Value, 14d));
            Assert.Contains(verticalTrigger.Setters.OfType<Setter>(), setter =>
                setter.Property == Control.TemplateProperty);
            Assert.DoesNotContain(verticalTrigger.Setters.OfType<Setter>(), setter => setter.Property == FrameworkElement.HeightProperty);

            window.Close();
        });
    }

    [Fact]
    public void ContextHelp_UsesNativeWrappingToolTipsOnAllFiveHelpButtons()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);

            foreach (var name in new[] { "IgnoredWindowsHelpButton", "DisconnectTemplateHelpButton", "ProcessExitedTemplateHelpButton", "DeadStartedTemplateHelpButton", "CloudKeyHelpButton" })
            {
                var help = Assert.IsType<Button>(window.FindName(name));
                Assert.True(help.Focusable);
                Assert.True(help.IsTabStop);
                Assert.Equal(System.Windows.Media.Colors.Transparent, Assert.IsType<System.Windows.Media.SolidColorBrush>(help.Background).Color);
                var toolTip = Assert.IsType<ToolTip>(help.ToolTip);

                Assert.Equal(PlacementMode.Bottom, toolTip.Placement);
                Assert.Equal(250, ToolTipService.GetInitialShowDelay(help));
                Assert.Equal(12000, ToolTipService.GetShowDuration(help));
                Assert.Equal(PlacementMode.Bottom, ToolTipService.GetPlacement(help));
                Assert.True(help.MinWidth >= 24);
                Assert.True(help.MinHeight >= 20);
                Assert.NotNull(toolTip.ContentTemplate);
            }

            Assert.Null(typeof(MainWindow).GetMethod("OnHelpButtonClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
            Assert.Null(typeof(MainWindow).GetMethod("OpenHelpToolTip", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));

            window.Close();
        });
    }

    [Fact]
    public void TestAction_RequiresEnabledDeliveryAndStaysDisabledWhileBusy()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var deliveryEnabled = Assert.IsType<CheckBox>(window.FindName("DeliveryEnabledInput"));
            var action = Assert.IsType<Button>(window.FindName("TestNotificationButton"));
            var telegramMode = Assert.IsType<RadioButton>(window.FindName("TelegramDeliveryModeRadio"));
            var cloudMode = Assert.IsType<RadioButton>(window.FindName("CloudDeliveryModeRadio"));

            Assert.False(action.IsEnabled);
            Assert.False(telegramMode.IsEnabled);
            Assert.False(cloudMode.IsEnabled);

            deliveryEnabled.IsChecked = true;
            Assert.True(action.IsEnabled);
            Assert.True(telegramMode.IsEnabled);
            Assert.True(cloudMode.IsEnabled);

            InvokePrivateStateMethod(window, "SetBusyState", "Выполняю действие...");
            Assert.False(action.IsEnabled);

            deliveryEnabled.IsChecked = false;
            deliveryEnabled.IsChecked = true;
            Assert.False(action.IsEnabled);
            Assert.False(telegramMode.IsEnabled);
            Assert.False(cloudMode.IsEnabled);

            InvokePrivateStateMethod(window, "SetReadyState", "Готово.");
            Assert.True(action.IsEnabled);
            Assert.True(telegramMode.IsEnabled);
            Assert.True(cloudMode.IsEnabled);

            deliveryEnabled.IsChecked = false;
            Assert.False(action.IsEnabled);
            Assert.False(telegramMode.IsEnabled);
            Assert.False(cloudMode.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void TestAction_RequiresSavedDeliveryModeSelection()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var deliveryEnabled = Assert.IsType<CheckBox>(window.FindName("DeliveryEnabledInput"));
            var cloudMode = Assert.IsType<RadioButton>(window.FindName("CloudDeliveryModeRadio"));
            var action = Assert.IsType<Button>(window.FindName("TestNotificationButton"));

            deliveryEnabled.IsChecked = true;
            Assert.True(action.IsEnabled);

            cloudMode.IsChecked = true;
            Assert.False(action.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void ButtonAndDeliveryRadioStyles_ExposeInteractionAndDisabledStates()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var buttonStyle = Assert.IsType<Style>(window.Resources[typeof(Button)]);
            var buttonTemplate = Assert.IsType<ControlTemplate>(Assert.Single(
                buttonStyle.Setters.OfType<Setter>(), setter => setter.Property == Control.TemplateProperty).Value);

            foreach (var property in new[] { UIElement.IsMouseOverProperty, ButtonBase.IsPressedProperty, UIElement.IsKeyboardFocusedProperty, UIElement.IsEnabledProperty })
            {
                Assert.Contains(buttonTemplate.Triggers.OfType<Trigger>(), trigger => trigger.Property == property);
            }

            var radioStyle = Assert.IsType<Style>(window.Resources["DeliveryModeRadioButtonStyle"]);
            var radioTemplate = Assert.IsType<ControlTemplate>(Assert.Single(
                radioStyle.Setters.OfType<Setter>(), setter => setter.Property == Control.TemplateProperty).Value);
            foreach (var property in new[] { UIElement.IsMouseOverProperty, ToggleButton.IsCheckedProperty, UIElement.IsKeyboardFocusedProperty, UIElement.IsEnabledProperty })
            {
                Assert.Contains(radioTemplate.Triggers.OfType<Trigger>(), trigger => trigger.Property == property);
            }

            var disabledRadio = Assert.IsType<RadioButton>(window.FindName("TelegramDeliveryModeRadio"));
            disabledRadio.IsEnabled = false;
            disabledRadio.ApplyTemplate();
            Assert.Equal(window.Resources["MutedBrush"], disabledRadio.Foreground);

            var button = Assert.IsType<Button>(window.FindName("TestNotificationButton"));
            button.IsEnabled = false;
            Assert.Equal(System.Windows.Input.Cursors.Arrow, button.Cursor);
            window.Close();
        });
    }

    [Fact]
    public void Overview_DoesNotExposeLastScanBlock()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            Assert.Null(window.FindName("ScanValueText"));
            window.Close();
        });
    }

    [Fact]
    public void TestAction_StaysInDeliverySettingsAndIsHiddenForCloudMode()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            Assert.Null(window.FindName("DeliveryModeInput"));
            var telegramMode = Assert.IsType<RadioButton>(window.FindName("TelegramDeliveryModeRadio"));
            var cloudMode = Assert.IsType<RadioButton>(window.FindName("CloudDeliveryModeRadio"));
            var action = Assert.IsType<Button>(window.FindName("TestNotificationButton"));
            var settings = Assert.IsType<TabItem>(window.FindName("SettingsTab"));
            var deliveryCard = Assert.IsType<Border>(window.FindName("DeliveryCard"));
            var templatesCard = Assert.IsType<Border>(window.FindName("NotificationTemplatesCard"));
            var deliveryPanel = Assert.IsType<StackPanel>(window.FindName("DeliveryConfigPanel"));
            var deliveryEnabled = Assert.IsType<CheckBox>(window.FindName("DeliveryEnabledInput"));

            deliveryEnabled.IsChecked = true;
            telegramMode.IsChecked = true;

            cloudMode.IsChecked = true;
            Assert.Same(settings, FindAncestor<TabItem>(action));
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(Grid.GetRow(deliveryCard) < Grid.GetRow(templatesCard));
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(window.FindName("LocalDeliveryPanel")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(window.FindName("CloudDeliveryPanel")).Visibility);
            Assert.True(deliveryPanel.Children.IndexOf(action) > 0);
            window.Close();
        });
    }

    [Fact]
    public void DeliveryModeRadios_SwitchPanelsAndResolveLocalOrCloudForSave()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var deliveryEnabled = Assert.IsType<CheckBox>(window.FindName("DeliveryEnabledInput"));
            var telegramMode = Assert.IsType<RadioButton>(window.FindName("TelegramDeliveryModeRadio"));
            var cloudMode = Assert.IsType<RadioButton>(window.FindName("CloudDeliveryModeRadio"));

            deliveryEnabled.IsChecked = true;
            telegramMode.IsChecked = true;
            Assert.Equal("Local", InvokePrivateStringMethod(window, "ResolveDeliveryModeForSave"));
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(window.FindName("LocalDeliveryPanel")).Visibility);

            cloudMode.IsChecked = true;
            Assert.Equal("Cloud", InvokePrivateStringMethod(window, "ResolveDeliveryModeForSave"));
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(window.FindName("CloudDeliveryPanel")).Visibility);

            deliveryEnabled.IsChecked = false;
            Assert.Equal("Disabled", InvokePrivateStringMethod(window, "ResolveDeliveryModeForSave"));
            window.Close();
        });
    }

    [Fact]
    public void DeliveryHint_HidesTechnicalSecretSaveCopyWhenEnabled()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var enabled = Assert.IsType<CheckBox>(window.FindName("DeliveryEnabledInput"));
            var hint = Assert.IsType<TextBlock>(window.FindName("DeliveryDisabledHintText"));

            enabled.IsChecked = true;

            Assert.Equal(Visibility.Collapsed, hint.Visibility);
            Assert.DoesNotContain("сохраняются отдельно", hint.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("несохранённые поля", hint.Text, StringComparison.OrdinalIgnoreCase);
            window.Close();
        });
    }

    [Fact]
    public void ConfirmationRefresh_PreservesDirtyFormWhileApplyingDeliveryMetadata()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var ignoredWindows = Assert.IsType<TextBox>(window.FindName("IgnoredWindowTitlesInput"));
            ignoredWindows.Text = "draft filter";

            var response = new SettingsResponseDto(
                new ApiVersionDto("1.0", "1.0"),
                45631,
                new LocalControlSettingsDto(45631, 1000, 5, 0, "Cloud", null, false, 42, "server filter", true, "server ghost", null, true, "server exited", true, "server dead"),
                new SecretPresenceDto(true, true, false));

            typeof(MainWindow)
                .GetMethod("ApplySettingsIfAllowed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [response, false]);

            Assert.Equal("draft filter", ignoredWindows.Text);
            window.Close();
        });
    }

    [Fact]
    public void ConfirmTelegramChatLinkAsync_DoesNotOverwriteDirtySettingsDuringRefresh()
    {
        RunInSta(() =>
        {
            var client = new LocalControlApiClient(
                new HttpClient(new ConfirmTelegramLinkHandler()),
                new FixedBootstrapDiscovery());
            var window = new MainWindow(client);
            var ignoredWindows = Assert.IsType<TextBox>(window.FindName("IgnoredWindowTitlesInput"));

            ignoredWindows.Text = "draft filter";
            InvokePrivateTaskMethod(window, "ConfirmTelegramChatLinkAsync").GetAwaiter().GetResult();

            Assert.Equal("draft filter", ignoredWindows.Text);
            window.Close();
        });
    }

    [Fact]
    public void MainSurfaces_UseDistinctDarkNonBlackLayers()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var windowBrush = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.Background);
            var cardBrush = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.Resources["CardBrush"]);
            var input = Assert.IsType<TextBox>(window.FindName("IgnoredWindowTitlesInput"));
            var inputBrush = Assert.IsType<System.Windows.Media.SolidColorBrush>(input.Background);
            var radio = Assert.IsType<RadioButton>(window.FindName("TelegramDeliveryModeRadio"));

            AssertDarkNonBlack(windowBrush.Color);
            AssertDarkNonBlack(cardBrush.Color);
            AssertDarkNonBlack(inputBrush.Color);
            Assert.NotEqual(windowBrush.Color, cardBrush.Color);
            Assert.NotEqual(cardBrush.Color, inputBrush.Color);
            var radioStyle = Assert.IsType<Style>(radio.Style);
            Assert.Contains(radioStyle.Setters.OfType<Setter>(), setter =>
                setter.Property == Control.ForegroundProperty && Equals(setter.Value, window.Resources["TextBrush"]));
            window.Close();
        });
    }

    [Fact]
    public void CalculateOverviewSplitDistance_ClampsForSmallInitialHeight()
    {
        var distance = MainWindow.CalculateOverviewSplitDistance(
            totalHeight: 150,
            splitterWidth: 4,
            panel1MinSize: 220,
            panel2MinSize: 220,
            preferredTopHeight: 280);

        Assert.Equal(0, distance);
    }

    [Fact]
    public void CloudBackendAddress_IsNotExposedInWindow()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);

            Assert.Null(window.FindName("CloudBackendBaseUrlInput"));

            window.Close();
        });
    }

    [Fact]
    public void MainWindow_UsesProductIcon()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);

            Assert.NotNull(window.Icon);

            window.Close();
        });
    }

    [Fact]
    public void TryCreateTraySettingsRequest_NormalizesVisibleTrayFields()
    {
        var succeeded = MainWindow.TryCreateTraySettingsRequest(
            deliveryMode: "Local",
            telegramDeliveryEnabled: true,
            ignoredWindowTitlesText: "  splash  \r\n\r\n updater ",
            disconnectNotificationEnabled: false,
            ghostDisconnectMessageTemplate: " ghost {pid} ",
            processExitedNotificationEnabled: true,
            processExitedMessageTemplate: " exit {windowTitle} ",
            deadStartedNotificationEnabled: false,
            deadStartedMessageTemplate: " dead {name} ",
            out var request,
            out var validationMessage);

        Assert.True(succeeded);
        Assert.Null(validationMessage);
        Assert.NotNull(request);
        Assert.Equal(new UpdateTraySettingsRequestDto(new TrayEditableSettingsDto(
            "Local",
            true,
            "splash" + Environment.NewLine + "updater",
            false,
            "ghost {pid}",
            true,
            "exit {windowTitle}",
            false,
            "dead {name}",
            BackendBaseUrl: null)), request);
    }

    [Fact]
    public void TryCreateTraySettingsRequest_LeavesCloudAddressToPackagedConfiguration()
    {
        var succeeded = MainWindow.TryCreateTraySettingsRequest(
            deliveryMode: "Cloud",
            telegramDeliveryEnabled: false,
            ignoredWindowTitlesText: null,
            disconnectNotificationEnabled: true,
            ghostDisconnectMessageTemplate: null,
            processExitedNotificationEnabled: true,
            processExitedMessageTemplate: null,
            deadStartedNotificationEnabled: true,
            deadStartedMessageTemplate: null,
            out var request,
            out var validationMessage);

        Assert.True(succeeded);
        Assert.Null(validationMessage);
        Assert.NotNull(request);
        Assert.Null(request.Settings.BackendBaseUrl);
    }

    [Fact]
    public void NotificationToggle_DisablesMatchingTemplateInput()
    {
        RunInSta(() =>
        {
            var window = new MainWindow(apiClient: null!);
            var disconnectToggle = Assert.IsType<CheckBox>(window.FindName("DisconnectNotificationEnabledInput"));
            var disconnectTemplate = Assert.IsType<TextBox>(window.FindName("GhostDisconnectMessageTemplateInput"));
            var processToggle = Assert.IsType<CheckBox>(window.FindName("ProcessExitedNotificationEnabledInput"));
            var processTemplate = Assert.IsType<TextBox>(window.FindName("ProcessExitedMessageTemplateInput"));
            var deadToggle = Assert.IsType<CheckBox>(window.FindName("DeadStartedNotificationEnabledInput"));
            var deadTemplate = Assert.IsType<TextBox>(window.FindName("DeadStartedMessageTemplateInput"));

            disconnectToggle.IsChecked = false;
            processToggle.IsChecked = false;
            deadToggle.IsChecked = false;

            Assert.False(disconnectTemplate.IsEnabled);
            Assert.False(processTemplate.IsEnabled);
            Assert.False(deadTemplate.IsEnabled);

            window.Close();
        });
    }

    [Fact]
    public void TryCreateSettingsRequest_TrimsAndParsesOptionalValues()
    {
        var succeeded = MainWindow.TryCreateSettingsRequest(
            loopbackPort: 45631,
            pollIntervalMs: 1000,
            idleTimeoutSec: 5,
            minConfirmLifetimeSec: 0,
            deliveryMode: "Cloud",
            backendBaseUrl: " https://example.test/api ",
            telegramDeliveryEnabled: true,
            telegramChatId: " 123456789 ",
            out var request,
            out var validationMessage);

        Assert.True(succeeded);
        Assert.Null(validationMessage);
        Assert.NotNull(request);
        Assert.Equal(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
            45631,
            1000,
            5,
            0,
            "Cloud",
            "https://example.test/api",
            true,
            123456789)), request);
    }

    [Fact]
    public void TryCreateSettingsRequest_NormalizesMessageSettingsAndIgnoreWindows()
    {
        var succeeded = MainWindow.TryCreateSettingsRequest(
            loopbackPort: 45631,
            pollIntervalMs: 1000,
            idleTimeoutSec: 5,
            minConfirmLifetimeSec: 0,
            deliveryMode: "Local",
            backendBaseUrl: null,
            telegramDeliveryEnabled: true,
            telegramChatId: "42",
            ignoredWindowTitlesText: "  splash  \r\n\r\n updater ",
            ghostDisconnectMessageTemplate: " ghost {pid} ",
            clientDisconnectedMessageTemplate: " disc {name} ",
            processExitedMessageTemplate: " exit {windowTitle} ",
            out var request,
            out var validationMessage);

        Assert.True(succeeded);
        Assert.Null(validationMessage);
        Assert.NotNull(request);
        Assert.Equal(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
            45631,
            1000,
            5,
            0,
            "Local",
            null,
            true,
            42,
            "splash" + Environment.NewLine + "updater",
            "ghost {pid}",
            "disc {name}",
            "exit {windowTitle}")), request);
    }

    [Fact]
    public void ResolveDisconnectTemplateText_PrefersVisibleTrayTemplateOverHiddenLegacyValue()
    {
        var text = MainWindow.ResolveDisconnectTemplateText(new LocalControlSettingsDto(
            LoopbackPort: 45631,
            PollIntervalMs: 1000,
            IdleTimeoutSec: 5,
            MinConfirmLifetimeSec: 0,
            DeliveryMode: "Local",
            BackendBaseUrl: null,
            TelegramDeliveryEnabled: true,
            TelegramChatId: 42,
            IgnoredWindowTitlesText: null,
            GhostDisconnectMessageTemplate: "ghost text",
            ClientDisconnectedMessageTemplate: "disconnect text",
            ProcessExitedMessageTemplate: null));

        Assert.Equal("ghost text", text);
    }

    [Fact]
    public void FormatTrayTimestamp_UsesLocalClockWithoutTimezoneSuffix()
    {
        var formatted = MainWindow.FormatTrayTimestamp(DateTimeOffset.Parse("2026-07-08T12:34:56+00:00"));

        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", formatted);
        Assert.DoesNotContain("+", formatted);
        Assert.DoesNotContain("-", formatted[11..]);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("ABCD-1234", true)]
    public void HasPendingTelegramLinkCode_TracksWhetherSelectionShouldBeEnabled(string? code, bool expected)
    {
        Assert.Equal(expected, MainWindow.HasPendingTelegramLinkCode(code));
    }

    [Fact]
    public void AutostartToggle_ReflectsAndUpdatesWindowsPreference()
    {
        RunInSta(() =>
        {
            var autostart = new StubAutostartService(initiallyEnabled: true);
            var window = new MainWindow(apiClient: null!, autostartService: autostart);
            var toggle = Assert.IsType<CheckBox>(window.FindName("AutostartInput"));

            Assert.True(toggle.IsChecked);

            toggle.IsChecked = false;

            Assert.False(autostart.Enabled);
            Assert.Equal(1, autostart.SetCallCount);
            window.Close();
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? captured = null;
        using var completed = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                action();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completed.Wait();
        thread.Join();

        if (captured is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA test failed: {captured}");
        }
    }

    private static T? FindAncestor<T>(FrameworkElement element)
        where T : FrameworkElement
    {
        for (var current = element.Parent as FrameworkElement; current is not null; current = current.Parent as FrameworkElement)
        {
            if (current is T typed)
            {
                return typed;
            }
        }

        return null;
    }

    private static void AssertDarkNonBlack(System.Windows.Media.Color color)
    {
        Assert.NotEqual(System.Windows.Media.Colors.Black, color);
        Assert.True((color.R * 0.2126) + (color.G * 0.7152) + (color.B * 0.0722) < 80);
    }

    private static void InvokePrivateStateMethod(MainWindow window, string methodName, string message) =>
        typeof(MainWindow)
            .GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [message]);

    private static string InvokePrivateStringMethod(MainWindow window, string methodName) =>
        Assert.IsType<string>(typeof(MainWindow)
            .GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null));

    private static Task InvokePrivateTaskMethod(MainWindow window, string methodName) =>
        Assert.IsAssignableFrom<Task>(typeof(MainWindow)
            .GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null));

    private sealed class FixedBootstrapDiscovery : AgentBootstrapDiscovery
    {
        public FixedBootstrapDiscovery() : base("unused") { }

        public override AgentBootstrapSnapshot Load() =>
            new(new Uri("http://127.0.0.1:45631/"), "token");
    }

    private sealed class StubAutostartService(bool initiallyEnabled) : IAutostartService
    {
        public bool Enabled { get; private set; } = initiallyEnabled;
        public int SetCallCount { get; private set; }

        public bool IsEnabled() => Enabled;

        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            SetCallCount++;
        }
    }

    private sealed class ConfirmTelegramLinkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object payload = request.RequestUri!.AbsolutePath switch
            {
                "/v1/actions/telegram-link/confirm" => new ActionResponseDto(
                    new ApiVersionDto("1.0", "1.0"),
                    new AgentActionResultDto("telegram_link_confirm", "pending", "Waiting for the Telegram code.", DateTimeOffset.UtcNow)),
                "/v1/status" => new StatusResponseDto(
                    new ApiVersionDto("1.0", "1.0"), "Running", "Cloud", 45631, 0, 0, DateTimeOffset.UtcNow, null,
                    new ComponentHealthDto("configured", "configured"), new ComponentHealthDto("healthy", "healthy"), null, null, Array.Empty<AgentConnectionDto>()),
                "/v1/settings" => new SettingsResponseDto(
                    new ApiVersionDto("1.0", "1.0"), 45631,
                    new LocalControlSettingsDto(45631, 1000, 5, 0, "Cloud", null, false, 42, "server filter", true, "server ghost", null, true, "server exited", true, "server dead"),
                    new SecretPresenceDto(true, true, false)),
                "/v1/incidents" => new IncidentsResponseDto(new ApiVersionDto("1.0", "1.0"), Array.Empty<AgentIncidentDto>()),
                _ => throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}"),
            };
            var json = JsonSerializer.Serialize(payload);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
