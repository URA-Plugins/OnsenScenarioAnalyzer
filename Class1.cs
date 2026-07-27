using Gallop;
using Gallop.Endpoints;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;

namespace OnsenScenarioAnalyzer
{
    public class OnsenScenarioAnalyzer : IPlugin
    {
        const string WorkspaceTitle = "OnsenScenarioAnalyzer";
        const string TrainingPanelKey = "training";

        Workspace? workspace;
        bool hasPublishedTrainingPanel;

        static readonly string DataDirectory = Path.Combine("PluginData", "OnsenScenarioAnalyzer");

        public void Initialize(IPluginContext context)
        {
            hasPublishedTrainingPanel = false;
            Handler.DataDirectory = DataDirectory;
            Directory.CreateDirectory(Handler.DataDirectory);
        }

        public void Dispose()
        {
            if (!hasPublishedTrainingPanel || workspace is not { } publishedWorkspace)
                return;

            publishedWorkspace.RemovePanel(TrainingPanelKey);
            hasPublishedTrainingPanel = false;
        }

        [ResponseAnalyzer<GameApi.SingleModeOnsen.CheckEvent>(1)]
        public ValueTask Analyzer(SingleModeOnsenCheckEventResponse @event)
        {
            var data = @event.data;
            if (data.chara_info.scenario_id != 12) return ValueTask.CompletedTask;
            var state = data.chara_info.state;
            if (data.home_info?.command_info_array is not null && !(state is 2 or 3)) //根据文本简单过滤防止重复、异常输出
            {
                if ((@event.data.unchecked_event_array != null && @event.data.unchecked_event_array.Length > 0) || @event.data.race_start_info != null) return ValueTask.CompletedTask;
                if (Handler.GetCommandInfoStage_legend(@event) == 0)
                    return ValueTask.CompletedTask;

                PublishTrainingPanel(Handler.ParseOnsenCommandInfo(@event));
            }

            return ValueTask.CompletedTask;
        }

        void PublishTrainingPanel(WorkspaceContent content)
        {
            var workspace = this.workspace ??= Workspace.Create(WorkspaceTitle);
            workspace.SetPanel(
                TrainingPanelKey,
                "训练分析",
                content,
                fullBleed: true,
                switchToWorkspace: !hasPublishedTrainingPanel);
            hasPublishedTrainingPanel = true;
        }
    }
}
