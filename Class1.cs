using System.Text.Json;
using System.Text.Json.Serialization;
using Gallop;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace OnsenScenarioAnalyzer;

public class OnsenScenarioAnalyzer : IPlugin
{
    const string WorkspaceTitle = "OnsenScenarioAnalyzer";
    const string TrainingPanelKey = "training";
    const string SettingsFileName = "settings.json";
    const int DefaultHistoryLimit = 100;
    const int MaximumHistoryLimit = 1000;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    readonly object historyGate = new();
    readonly List<HistoryEntry> history = [];

    IApplication? application;
    Workspace? workspace;
    HistoryView? historyView;
    WorkspaceContent? historyPanelContent;
    WorkspaceContent? liveSnapshot;
    int historyLimit = DefaultHistoryLimit;
    int selectedIndex = -1;
    long displayVersion;
    long generation;
    bool hasUnread;
    bool hasPublishedTrainingPanel;
    bool disposed = true;

    static readonly string DataDirectory = Path.Combine("PluginData", "OnsenScenarioAnalyzer");
    string SettingsPath => Path.Combine(DataDirectory, SettingsFileName);

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Analyzers.Register<SingleModeOnsenCheckEventResponse>(
            AnalyzerKind.Response,
            [EndpointPattern.Exact("/umamusume/single_mode_onsen/check_event")],
            invocation => Analyzer(invocation.Payload),
            priority: 1);
        var settings = LoadSettings();
        lock (historyGate)
        {
            if (!disposed)
                throw new InvalidOperationException("OnsenScenarioAnalyzer 已初始化。");

            application = context.Application;
            history.Clear();
            historyView = null;
            historyPanelContent = null;
            liveSnapshot = null;
            historyLimit = settings.HistoryLimit;
            selectedIndex = -1;
            displayVersion = 0;
            generation++;
            hasUnread = false;
            hasPublishedTrainingPanel = false;
            disposed = false;
        }

        Handler.DataDirectory = DataDirectory;
        Directory.CreateDirectory(Handler.DataDirectory);
    }

    public void Dispose()
    {
        HistoryView? view;
        Workspace? publishedWorkspace;
        lock (historyGate)
        {
            if (disposed)
                return;

            disposed = true;
            generation++;
            history.Clear();
            liveSnapshot = null;
            selectedIndex = -1;
            hasUnread = false;
            application = null;
            view = historyView;
            historyView = null;
            historyPanelContent = null;
            publishedWorkspace = hasPublishedTrainingPanel ? workspace : null;
            hasPublishedTrainingPanel = false;
        }

        view?.Stop();
        publishedWorkspace?.RemovePanel(TrainingPanelKey);
    }

    public async Task ConfigPromptAsync(
        IApplication application,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        cancellationToken.ThrowIfCancellationRequested();
        if (application.TopRunnable is null &&
            Environment.CurrentManagedThreadId != application.MainThreadId)
        {
            throw new InvalidOperationException(
                "OnsenScenarioAnalyzer 无法从非 UI thread 启动配置：Terminal.Gui 当前没有正在运行的 session。");
        }

        var draft = LoadSettings();
        HistorySettings? saved;
        if (Environment.CurrentManagedThreadId == application.MainThreadId)
        {
            saved = RunConfigDialog(application, draft, cancellationToken);
        }
        else
        {
            var completion = new TaskCompletionSource<HistorySettings?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            application.Invoke(() =>
            {
                try
                {
                    completion.SetResult(RunConfigDialog(application, draft, cancellationToken));
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
            saved = await completion.Task;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (saved is null)
            throw new OperationCanceledException("OnsenScenarioAnalyzer 配置已取消。", cancellationToken);

        SaveSettings(saved);
        ApplyHistoryLimit(saved.HistoryLimit);
    }

    ValueTask Analyzer(SingleModeOnsenCheckEventResponse @event)
    {
        var data = @event.data;
        if (data.chara_info.scenario_id != 12)
            return ValueTask.CompletedTask;

        var state = data.chara_info.state;
        if (data.home_info?.command_info_array is not null && !(state is 2 or 3)) //根据文本简单过滤防止重复、异常输出
        {
            if ((data.unchecked_event_array is { Length: > 0 }) || data.race_start_info is not null)
                return ValueTask.CompletedTask;
            if (Handler.GetCommandInfoStage_legend(@event) == 0)
                return ValueTask.CompletedTask;

            long currentGeneration;
            lock (historyGate)
            {
                if (disposed)
                    return ValueTask.CompletedTask;
                currentGeneration = generation;
            }
            var key = new HistoryKey(data.chara_info.single_mode_chara_id, data.chara_info.turn);
            PublishTrainingPanel(currentGeneration, key, Handler.ParseOnsenCommandInfo(@event));
        }

        return ValueTask.CompletedTask;
    }

    void PublishTrainingPanel(long currentGeneration, HistoryKey key, WorkspaceContent content)
    {
        Workspace publishedWorkspace;
        WorkspaceContent selected;
        long version;
        var notifyUnread = false;
        lock (historyGate)
        {
            if (disposed || generation != currentGeneration)
                return;

            publishedWorkspace = workspace ??= Workspace.Create(WorkspaceTitle);
            if (!hasPublishedTrainingPanel)
            {
                var panelApplication = application
                    ?? throw new InvalidOperationException("OnsenScenarioAnalyzer 尚未初始化。");
                var panelContent = new WorkspaceContent(
                    () => CreateHistoryView(panelApplication, currentGeneration));
                historyPanelContent = panelContent;
                publishedWorkspace.SetPanel(
                    TrainingPanelKey,
                    "训练分析",
                    panelContent,
                    fullBleed: true,
                    switchToWorkspace: true);
                hasPublishedTrainingPanel = true;
            }

            liveSnapshot = content;
            if (historyLimit == 0)
            {
                history.Clear();
                selectedIndex = -1;
                hasUnread = false;
                selected = content;
            }
            else
            {
                var selectedKey = selectedIndex >= 0 && selectedIndex < history.Count
                    ? history[selectedIndex].Key
                    : (HistoryKey?)null;
                var wasViewingNewest = selectedIndex < 0 || selectedIndex == history.Count - 1;
                var existingIndex = history.FindIndex(entry => entry.Key == key);
                if (existingIndex >= 0)
                {
                    history[existingIndex] = new(key, content);
                }
                else
                {
                    history.Add(new(key, content));
                }

                TrimHistory(selectedKey, selectNewestForNewEntry: existingIndex < 0 && wasViewingNewest);
                if (existingIndex < 0 && !wasViewingNewest && selectedKey == history[selectedIndex].Key && !hasUnread)
                {
                    hasUnread = true;
                    notifyUnread = true;
                }
                selected = history[selectedIndex].Content;
            }
            version = ++displayVersion;
        }

        RefreshHistoryView(selected, publishedWorkspace, version);
        if (notifyUnread)
            publishedWorkspace.Notify("有新的训练分析记录，按 → 查看最新。", UiSeverity.Info);
    }

    void TrimHistory(HistoryKey? selectedKey, bool selectNewestForNewEntry = false)
    {
        var overflow = history.Count - historyLimit;
        if (overflow > 0)
            history.RemoveRange(0, overflow);

        if (history.Count == 0)
        {
            selectedIndex = -1;
            hasUnread = false;
            return;
        }
        if (selectNewestForNewEntry)
        {
            selectedIndex = history.Count - 1;
            hasUnread = false;
            return;
        }
        if (selectedKey is { } key)
        {
            selectedIndex = history.FindIndex(entry => entry.Key == key);
            if (selectedIndex >= 0)
                return;
        }

        selectedIndex = history.Count - 1;
        hasUnread = false;
    }

    void ApplyHistoryLimit(int value)
    {
        WorkspaceContent? selected;
        Workspace? publishedWorkspace;
        long version;
        lock (historyGate)
        {
            historyLimit = value;
            var selectedKey = selectedIndex >= 0 && selectedIndex < history.Count
                ? history[selectedIndex].Key
                : (HistoryKey?)null;
            if (value == 0)
            {
                history.Clear();
                selectedIndex = -1;
                hasUnread = false;
            }
            else
            {
                TrimHistory(selectedKey);
            }

            selected = value == 0
                ? liveSnapshot
                : selectedIndex >= 0 ? history[selectedIndex].Content : liveSnapshot;
            publishedWorkspace = hasPublishedTrainingPanel ? workspace : null;
            version = ++displayVersion;
        }

        if (selected is not null && publishedWorkspace is not null)
            RefreshHistoryView(selected, publishedWorkspace, version);
    }

    void RefreshHistoryView(
        WorkspaceContent content,
        Workspace publishedWorkspace,
        long version)
    {
        IApplication? app;
        WorkspaceContent? panelContent;
        long currentGeneration;
        lock (historyGate)
        {
            app = disposed ? null : application;
            panelContent = historyPanelContent;
            currentGeneration = generation;
        }
        if (app is null || panelContent is null)
            return;

        app.Invoke(() =>
        {
            HistoryView? view;
            lock (historyGate)
            {
                if (disposed || generation != currentGeneration ||
                    displayVersion != version || !hasPublishedTrainingPanel)
                {
                    return;
                }
                view = historyView;
            }
            view?.Show(content, version);

            lock (historyGate)
            {
                if (!disposed && generation == currentGeneration &&
                    displayVersion == version && hasPublishedTrainingPanel)
                {
                    publishedWorkspace.SetPanel(
                        TrainingPanelKey,
                        "训练分析",
                        panelContent,
                        fullBleed: true,
                        switchToWorkspace: false);
                }
            }
        });
    }

    HistoryView CreateHistoryView(IApplication app, long panelGeneration)
    {
        WorkspaceContent? selected;
        long version;
        bool active;
        HistoryView view;
        lock (historyGate)
        {
            active = !disposed && generation == panelGeneration;
            selected = active
                ? historyLimit == 0
                    ? liveSnapshot
                    : selectedIndex >= 0 ? history[selectedIndex].Content : liveSnapshot
                : null;
            version = displayVersion;
            view = new(app, this, active);
            if (active)
                historyView = view;
        }

        if (selected is not null)
            view.Show(selected, version);
        return view;
    }

    bool HandleHistoryKey(Key key)
    {
        WorkspaceContent? selected = null;
        WorkspaceContent? panelContent = null;
        Workspace? currentWorkspace;
        HistoryView? view = null;
        long version = 0;
        int position = 0;
        int count = 0;
        lock (historyGate)
        {
            if (disposed || historyLimit == 0 || history.Count == 0 || workspace is null)
                return false;
            currentWorkspace = workspace;

            if (history.Count > 0)
            {
                if (key.KeyCode == Key.CursorUp.KeyCode)
                    selectedIndex = Math.Max(0, selectedIndex - 1);
                else if (key.KeyCode == Key.CursorDown.KeyCode)
                    selectedIndex = Math.Min(history.Count - 1, selectedIndex + 1);
                else if (key.KeyCode == Key.CursorLeft.KeyCode)
                    selectedIndex = 0;
                else if (key.KeyCode == Key.CursorRight.KeyCode)
                    selectedIndex = history.Count - 1;
                if (selectedIndex == history.Count - 1)
                    hasUnread = false;
                selected = history[selectedIndex].Content;
                panelContent = historyPanelContent;
                view = historyView;
                version = ++displayVersion;
                position = selectedIndex + 1;
                count = history.Count;
            }
        }

        if (selected is not null)
            view?.Show(selected, version);
        lock (historyGate)
        {
            if (!disposed && displayVersion == version &&
                hasPublishedTrainingPanel && panelContent is not null)
            {
                currentWorkspace.SetPanel(
                    TrainingPanelKey,
                    "训练分析",
                    panelContent,
                    fullBleed: true,
                    switchToWorkspace: false);
            }
        }
        if (count > 0)
            currentWorkspace.Notify($"历史记录 {position}/{count}", UiSeverity.Info);
        return true;
    }

    bool IsHistoryKeyAvailable(HistoryView view, Key key)
    {
        if (key.Handled || key.IsCtrl || key.IsAlt || key.IsShift ||
            (key.KeyCode != Key.CursorUp.KeyCode &&
             key.KeyCode != Key.CursorDown.KeyCode &&
             key.KeyCode != Key.CursorLeft.KeyCode &&
             key.KeyCode != Key.CursorRight.KeyCode))
        {
            return false;
        }

        lock (historyGate)
        {
            if (disposed || historyLimit == 0 || workspace is null ||
                !ReferenceEquals(Workspace.Current, workspace))
            {
                return false;
            }
        }

        for (var focused = view.Application.TopRunnableView?.MostFocused;
             focused is not null;
             focused = focused.SuperView)
        {
            if (ReferenceEquals(focused, view))
                return true;
        }
        return false;
    }

    void HistoryViewDisposed(HistoryView view)
    {
        lock (historyGate)
        {
            if (ReferenceEquals(historyView, view))
                historyView = null;
        }
    }

    HistorySettings LoadSettings()
    {
        if (!File.Exists(SettingsPath))
            return new() { HistoryLimit = DefaultHistoryLimit };

        try
        {
            var settings = JsonSerializer.Deserialize<HistorySettings>(
                File.ReadAllText(SettingsPath),
                JsonOptions) ?? throw new JsonException("根值不能为 null。");
            ValidateHistoryLimit(settings.HistoryLimit);
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"无法读取 {SettingsPath}: {exception.Message}", exception);
        }
    }

    void SaveSettings(HistorySettings settings)
    {
        ValidateHistoryLimit(settings.HistoryLimit);
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    static void ValidateHistoryLimit(int value)
    {
        if (value is < 0 or > MaximumHistoryLimit)
        {
            throw new InvalidDataException(
                $"historyLimit 必须在 0 到 {MaximumHistoryLimit} 之间，实际为 {value}。");
        }
    }

    static HistorySettings? RunConfigDialog(
        IApplication application,
        HistorySettings draft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var dialog = new Dialog
        {
            Title = "OnsenScenarioAnalyzer 配置",
            Width = 60,
            Height = 10
        };
        dialog.Add(new Label
        {
            X = 1,
            Y = 0,
            Text = $"History 上限 (0-{MaximumHistoryLimit})"
        });
        var limit = new TextField
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = draft.HistoryLimit.ToString()
        };
        var validation = new Label
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill(1),
            Height = 2
        };
        dialog.Add(limit, validation);

        HistorySettings? saved = null;
        var save = new Button { Text = "保存", IsDefault = true };
        save.Accepting += (_, e) =>
        {
            if (!int.TryParse(limit.Text, out var value) || value is < 0 or > MaximumHistoryLimit)
            {
                validation.Text = $"History 上限必须是 0 到 {MaximumHistoryLimit} 之间的整数。";
                e.Handled = true;
                return;
            }

            saved = new() { HistoryLimit = value };
            application.RequestStop(dialog);
            e.Handled = true;
        };
        var cancel = new Button { Text = "取消" };
        cancel.Accepting += (_, e) =>
        {
            application.RequestStop(dialog);
            e.Handled = true;
        };
        dialog.AddButton(cancel);
        dialog.AddButton(save);
        limit.SetFocus();

        using (cancellationToken.Register(
                   () => application.Invoke(() => application.RequestStop(dialog))))
        {
            application.Run(dialog);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return saved;
    }

    readonly record struct HistoryKey(int SingleModeCharaId, int Turn);
    sealed record HistoryEntry(HistoryKey Key, WorkspaceContent Content);

    sealed class HistorySettings
    {
        [JsonRequired]
        public int HistoryLimit { get; init; }
    }

    sealed class HistoryView : View
    {
        readonly object viewGate = new();
        readonly OnsenScenarioAnalyzer owner;
        View? contentView;
        long displayedVersion = -1;
        bool stopped;

        internal HistoryView(
            IApplication application,
            OnsenScenarioAnalyzer owner,
            bool active)
        {
            Application = application;
            this.owner = owner;
            Width = Dim.Fill();
            Height = Dim.Auto();
            CanFocus = true;
            TabStop = TabBehavior.TabGroup;
            stopped = !active;
            if (active)
                application.Keyboard.KeyDown += ApplicationKeyDown;
        }

        internal IApplication Application { get; }

        internal void Show(WorkspaceContent content, long version)
        {
            lock (viewGate)
            {
                if (stopped || version <= displayedVersion)
                    return;

                var next = content.CreateView();
                next.X = 0;
                next.Y = 0;
                next.Width = Dim.Fill();
                if (contentView is { } previous)
                {
                    Remove(previous);
                    previous.Dispose();
                }
                contentView = next;
                displayedVersion = version;
                Add(next);
                SetNeedsLayout();
                SetNeedsDraw();
            }
        }

        internal void Stop()
        {
            lock (viewGate)
            {
                if (stopped)
                    return;
                stopped = true;
                Application.Keyboard.KeyDown -= ApplicationKeyDown;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
                owner.HistoryViewDisposed(this);
            }
            base.Dispose(disposing);
        }

        void ApplicationKeyDown(object? sender, Key key)
        {
            if (!owner.IsHistoryKeyAvailable(this, key))
                return;

            key.Handled = owner.HandleHistoryKey(key);
        }
    }
}
