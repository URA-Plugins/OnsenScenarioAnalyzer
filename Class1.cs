using Newtonsoft.Json.Linq;
using Spectre.Console;
using System.IO.Compression;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Plugin;

namespace OnsenScenarioAnalyzer
{
    public class OnsenScenarioAnalyzer : IPlugin
    {
        [PluginDescription("解析温泉杯回合信息")]
        public string Name => "OnsenScenarioAnalyzer";
        public string Author => "离披";
        public Version Version => new(1, 0, 0);
        public string[] Targets => ["Cygames"];
        public async Task UpdatePlugin(ProgressContext ctx)
        {
            var progress = ctx.AddTask($"[[{Name}]] 更新");

            using var client = new HttpClient();
            using var resp = await client.GetAsync($"https://api.github.com/repos/URA-Plugins/{Name}/releases/latest");
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JObject.Parse(json);

            var isLatest = ("v" + Version.ToString()).Equals("v" + jo["tag_name"]?.ToString());
            if (isLatest)
            {
                progress.Increment(progress.MaxValue);
                progress.StopTask();
                return;
            }
            progress.Increment(25);

            var downloadUrl = jo["assets"][0]["browser_download_url"].ToString();
            if (Config.Updater.IsGithubBlocked && !Config.Updater.ForceUseGithubToUpdate)
            {
                downloadUrl = downloadUrl.Replace("https://", "https://gh.shuise.dev/");
            }
            using var msg = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            using var stream = await msg.Content.ReadAsStreamAsync();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                    break;
                progress.Increment(read / msg.Content.Headers.ContentLength ?? 1 * 0.5);
            }
            using var archive = new ZipArchive(stream);
            archive.ExtractToDirectory(Path.Combine("Plugins", Name), true);
            progress.Increment(25);

            progress.StopTask();
        }

        [Analyzer(priority: 1)]
        public void Analyzer(JObject jo)
        {
            if (!jo.HasCharaInfo()) return;
            if (jo["data"] is null || jo["data"] is not JObject data) return;
            if (data["chara_info"] is null || data["chara_info"] is not JObject chara_info) return;
            if (chara_info["scenario_id"].ToInt() != (int)ScenarioType.Onsen) return;
            var state = chara_info["state"].ToInt();
            if (chara_info != null && data["home_info"]?["command_info_array"] != null && data["race_reward_info"].IsNull() && !(state is 2 or 3)) //根据文本简单过滤防止重复、异常输出
            {
                var @event = jo.ToObject<Gallop.SingleModeCheckEventResponse>();
                if ((@event.data.unchecked_event_array != null && @event.data.unchecked_event_array.Length > 0) || @event.data.race_start_info != null) return;
                Handler.ParseOnsenCommandInfo(@event);
            }
        }
    }
}
