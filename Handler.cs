using EventLoggerPlugin;
using Gallop;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UmamusumeResponseAnalyzer.Game.TurnInfo;
using UmamusumeResponseAnalyzer.LocalizedLayout.Handlers;
using static OnsenScenarioAnalyzer.i18n.Game;

namespace OnsenScenarioAnalyzer
{
    public static class Handler
    {
        public static int GetCommandInfoStage_legend(SingleModeCheckEventResponse @event)
        {
            //if ((@event.data.unchecked_event_array != null && @event.data.unchecked_event_array.Length > 0)) return;
            if (@event.data.chara_info.playing_state == 1 && (@event.data.unchecked_event_array == null || @event.data.unchecked_event_array.Length == 0))
            {
                return 2;
            } //常规训练
            else if (@event.data.chara_info.playing_state == 5 && @event.data.unchecked_event_array.Any(x => x.story_id == 400010112)) //选buff
            {
                return 5;
            }
            else if (@event.data.chara_info.playing_state == 5 &&
                (@event.data.unchecked_event_array.Any(x => x.story_id == 830241003))) //选团卡事件
            {
                return 3;
            }
            else
            {
                return 0;
            }
        }

        // 当前生效的温泉buff是否为超回复
        public static bool isCurrentBuffSuper = false;
        // 上次的温泉buff情况
        public static SingleModeOnsenBathingInfo lastBathing = new();
        // 上次的事件数量
        public static int lastEventCount = 0;
        // 给超回复的事件ID
        public static int[] superEvents = { 809050011, 809050012, 809050013, 809050014, 809050015 };
        public static void ParseOnsenCommandInfo(SingleModeCheckEventResponse @event)
        {
            var stage = GetCommandInfoStage_legend(@event);
            if (stage == 0)
                return;
            var layout = new Layout().SplitColumns(
                new Layout("Main").Size(CommandInfoLayout.Current.MainSectionWidth).SplitRows(
                    new Layout("体力干劲条").SplitColumns(
                        new Layout("日期").Ratio(4),
                        new Layout("总属性").Ratio(6),
                        new Layout("体力").Ratio(6),
                        new Layout("干劲").Ratio(3)).Size(3),
                    new Layout("重要信息").Size(5),
                    new Layout("剧本信息").SplitColumns(
                        new Layout("温泉券").Ratio(1),
                        new Layout("温泉Buff").Ratio(1),
                        new Layout("超回复").Ratio(1),
                        new Layout("挖掘进度").Ratio(1)
                        ).Size(3),
                    //new Layout("分割", new Rule()).Size(1),
                    new Layout("训练信息")  // size 20, 共约30行
                    ).Ratio(4),
                new Layout("Ext").Ratio(1)
                );
            var noTrainingTable = false;
            var critInfos = new List<string>();
            var turn = new TurnInfoOnsen(@event.data);
            var dataset = @event.data.onsen_data_set;

            if (GameStats.currentTurn != turn.Turn - 1 //正常情况
                && GameStats.currentTurn != turn.Turn //重复显示
                && turn.Turn != 1 //第一个回合
                )
            {
                GameStats.isFullGame = false;
                critInfos.Add(string.Format(I18N_WrongTurnAlert, GameStats.currentTurn, turn.Turn));
                EventLogger.Init(@event);
                EventLogger.IsStart = true;
                isCurrentBuffSuper = false;
                lastEventCount = 0;
                // 初始化时根据温泉buff状态设置是否记录体力消耗
                if (turn.Turn <= 2 || turn.Turn > 72)
                {
                    EventLogger.captureVitalSpending = false;
                }
                else
                {
                    EventLogger.captureVitalSpending = true;
                }
            }
            else if (turn.Turn == 1)
            {
                GameStats.isFullGame = true;
                EventLogger.Init(@event);
                EventLogger.IsStart = true;
                isCurrentBuffSuper = false;
                lastEventCount = 0;
            }

            // 统计上回合事件
            var lastEvents = EventLogger.AllEvents
                    .Skip(lastEventCount)
                    .Select(x => x.StoryId)
                    .ToList();
            lastEventCount = EventLogger.AllEvents.Count;
            // 统计温泉Buff情况
            var bathing = dataset.bathing_info;
            if (bathing != null)
            {
                // 更新跟踪状态
                if (lastBathing.superior_state == 0 && bathing.superior_state > 0)
                {
                    EventLogger.captureVitalSpending = false;
                    if (lastEvents.Any(x => superEvents.Contains(x)))
                    {
                        AnsiConsole.MarkupLine("[magenta]友人提供超回复[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[magenta]触发超回复[/]");
                        EventLogger.vitalSpent = 0;
                    }                    
                }
                if (lastBathing.onsen_effect_remain_count == 0 && bathing.onsen_effect_remain_count == 2)
                {
                    AnsiConsole.MarkupLine("[magenta]使用温泉Buff[/]");
                    if (lastBathing.superior_state > 0) isCurrentBuffSuper = true;
                }
                if (isCurrentBuffSuper && bathing.onsen_effect_remain_count == 0 && bathing.superior_state == 0)
                {
                    AnsiConsole.MarkupLine("[magenta]超回复Buff结束，开始记录体力消耗[/]");
                    isCurrentBuffSuper = false;
                    EventLogger.captureVitalSpending = true;
                }
                lastBathing = bathing;

                // 显示当前状态
                layout["温泉券"].Update(new Panel($"[cyan]温泉券: {bathing.ticket_num} / 3[/]").Expand());
                if (bathing.onsen_effect_remain_count > 0)
                {
                    layout["温泉Buff"].Update(new Panel($"[lightgreen]温泉Buff剩余 {bathing.onsen_effect_remain_count} 回合[/]").Expand());
                }
                else
                {
                    layout["温泉Buff"].Update(new Panel($"温泉Buff未生效").Expand());
                }
                if (isCurrentBuffSuper) {
                    layout["超回复"].Update(new Panel($"[lightgreen]超回复生效中[/]").Expand());
                }
                else if (bathing.superior_state > 0)
                {
                    layout["超回复"].Update(new Panel($"[green]必定超回复[/]").Expand());
                }
                else
                {
                    layout["超回复"].Update(new Panel($"[blue]累计体力消耗: {EventLogger.vitalSpent}[/]").Expand());
                }

            }
            
            //买技能，大师杯剧本年末比赛，会重复显示
            if (@event.data.chara_info.playing_state != 1)
            {
                critInfos.Add(I18N_RepeatTurn);
            }
            else
            {
                //初始化TurnStats
                GameStats.whichScenario = @event.data.chara_info.scenario_id;
                GameStats.currentTurn = turn.Turn;
                GameStats.stats[turn.Turn] = new TurnStats();
                EventLogger.Update(@event);
            }
            // T3 在EventLogger更新后需要开始捕获体力消耗
            if (turn.Turn == 3)
            {
                EventLogger.captureVitalSpending = true;
            }
            var trainItems = new Dictionary<int, SingleModeCommandInfo>
            {
                { 101, @event.data.home_info.command_info_array[0] },
                { 105, @event.data.home_info.command_info_array[1] },
                { 102, @event.data.home_info.command_info_array[2] },
                { 103, @event.data.home_info.command_info_array[3] },
                { 106, @event.data.home_info.command_info_array[4] }
            };
            var trainStats = new TrainStats[5];
            var turnStat = @event.data.chara_info.playing_state != 1 ? new TurnStats() : GameStats.stats[turn.Turn];
            turnStat.motivation = @event.data.chara_info.motivation;
            var failureRate = new Dictionary<int, int>();

            // 总属性计算
            var currentFiveValue = new int[]
            {
                @event.data.chara_info.speed,
                @event.data.chara_info.stamina,
                @event.data.chara_info.power ,
                @event.data.chara_info.guts ,
                @event.data.chara_info.wiz ,
            };
            var fiveValueMaxRevised = new int[]
            {
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_speed),
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_stamina),
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_power) ,
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_guts) ,
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_wiz) ,
            };
            var currentFiveValueRevised = currentFiveValue.Select(ScoreUtils.ReviseOver1200).ToArray();
            var totalValue = currentFiveValueRevised.Sum();
            var totalValueWithPt = totalValue + @event.data.chara_info.skill_point;

            for (var i = 0; i < 5; i++)
            {
                var trainId = TurnInfoOnsen.TrainIds[i];
                failureRate[trainId] = trainItems[trainId].failure_rate;
                var trainParams = new Dictionary<int, int>()
                {
                    {1,0},
                    {2,0},
                    {3,0},
                    {4,0},
                    {5,0},
                    {30,0},
                    {10,0},
                };
                foreach (var item in turn.GetCommonResponse().home_info.command_info_array)
                {
                    if (TurnInfoOnsen.ToTrainId.TryGetValue(item.command_id, out var value) && value == trainId)
                    {
                        foreach (var trainParam in item.params_inc_dec_info_array)
                            trainParams[trainParam.target_type] += trainParam.value;
                    }
                }

                var stats = new TrainStats
                {
                    FailureRate = trainItems[trainId].failure_rate,
                    VitalGain = trainParams[10]
                };
                if (turn.Vital + stats.VitalGain > turn.MaxVital)
                    stats.VitalGain = turn.MaxVital - turn.Vital;
                if (stats.VitalGain < -turn.Vital)
                    stats.VitalGain = -turn.Vital;
                stats.FiveValueGain = [trainParams[1], trainParams[2], trainParams[3], trainParams[4], trainParams[5]];
                stats.PtGain = trainParams[30];

                var valueGainUpper = dataset.command_info_array.FirstOrDefault(x => x.command_id == trainId || x.command_id == TurnInfoOnsen.XiahesuIds[trainId])?.params_inc_dec_info_array;
                if (valueGainUpper != null)
                {
                    foreach (var item in valueGainUpper)
                    {
                        if (item.target_type == 30)
                            stats.PtGain += item.value;
                        else if (item.target_type <= 5)
                            stats.FiveValueGain[item.target_type - 1] += item.value;
                    }
                }

                for (var j = 0; j < 5; j++)
                    stats.FiveValueGain[j] = ScoreUtils.ReviseOver1200(turn.Stats[j] + stats.FiveValueGain[j]) - ScoreUtils.ReviseOver1200(turn.Stats[j]);

                if (turn.Turn == 1)
                {
                    turnStat.trainLevel[i] = 1;
                    turnStat.trainLevelCount[i] = 0;
                }
                else
                {
                    var lastTrainLevel = GameStats.stats[turn.Turn - 1] != null ? GameStats.stats[turn.Turn - 1].trainLevel[i] : 1;
                    var lastTrainLevelCount = GameStats.stats[turn.Turn - 1] != null ? GameStats.stats[turn.Turn - 1].trainLevelCount[i] : 0;

                    turnStat.trainLevel[i] = lastTrainLevel;
                    turnStat.trainLevelCount[i] = lastTrainLevelCount;
                    if (GameStats.stats[turn.Turn - 1] != null &&
                        GameStats.stats[turn.Turn - 1].playerChoice == TurnInfoOnsen.TrainIds[i] &&
                        !GameStats.stats[turn.Turn - 1].isTrainingFailed &&
                        !((turn.Turn - 1 >= 37 && turn.Turn - 1 <= 40) || (turn.Turn - 1 >= 61 && turn.Turn - 1 <= 64))
                        )//上回合点的这个训练，计数+1
                        turnStat.trainLevelCount[i] += 1;
                    if (turnStat.trainLevelCount[i] >= 4)
                    {
                        turnStat.trainLevelCount[i] -= 4;
                        turnStat.trainLevel[i] += 1;
                    }
                    //检查是否有剧本全体训练等级+1
                    if (turn.Turn == 25 || turn.Turn == 37 || turn.Turn == 49)
                        turnStat.trainLevelCount[i] += 4;
                    if (turnStat.trainLevelCount[i] >= 4)
                    {
                        turnStat.trainLevelCount[i] -= 4;
                        turnStat.trainLevel[i] += 1;
                    }

                    if (turnStat.trainLevel[i] >= 5)
                    {
                        turnStat.trainLevel[i] = 5;
                        turnStat.trainLevelCount[i] = 0;
                    }

                    var trainlv = @event.data.chara_info.training_level_info_array.First(x => x.command_id == TurnInfoOnsen.TrainIds[i]).level;
                    if (turnStat.trainLevel[i] != trainlv && stage == 2)
                    {
                        //可能是半途开启小黑板，也可能是有未知bug
                        critInfos.Add($"[red]警告：训练等级预测错误，预测{TurnInfoOnsen.TrainIds[i]}为lv{turnStat.trainLevel[i]}(+{turnStat.trainLevelCount[i]})，实际为lv{trainlv}[/]");
                        turnStat.trainLevel[i] = trainlv;
                        turnStat.trainLevelCount[i] = 0;//如果是半途开启小黑板，则会在下一次升级时变成正确的计数
                    }
                }

                trainStats[i] = stats;
            }
            if (stage == 2)
            {
                // 把训练等级信息更新到GameStats
                turnStat.fiveTrainStats = trainStats;
                GameStats.stats[turn.Turn] = turnStat;
            }

            //训练或比赛阶段
            if (stage == 2)
            {
                var grids = new Grid();
                grids.AddColumns(6);
                foreach (var column in grids.Columns)
                {
                    column.Padding = new Padding(0, 0, 0, 0);
                }

                var failureRateStr = new string[5];
                //失败率>=40%标红、>=20%(有可能大失败)标DarkOrange、>0%标黄
                for (var i = 0; i < 5; i++)
                {
                    var thisFailureRate = failureRate[TurnInfoOnsen.TrainIds[i]];
                    failureRateStr[i] = thisFailureRate switch
                    {
                        >= 40 => $"[red]({thisFailureRate}%)[/]",
                        >= 20 => $"[darkorange]({thisFailureRate}%)[/]",
                        > 0 => $"[yellow]({thisFailureRate}%)[/]",
                        _ => string.Empty
                    };
                }
                var commands = turn.CommandInfoArray.Select(command =>
                {
                    var table = new Table()
                    .AddColumn(command.TrainIndex switch
                    {
                        1 => $"{I18N_Speed}{failureRateStr[0]}",
                        2 => $"{I18N_Stamina}{failureRateStr[1]}",
                        3 => $"{I18N_Power}{failureRateStr[2]}",
                        4 => $"{I18N_Nuts}{failureRateStr[3]}",
                        5 => $"{I18N_Wiz}{failureRateStr[4]}",
                        6 => $"PR活动"
                    });

                    var currentStat = turn.StatsRevised[command.TrainIndex - 1];
                    var statUpToMax = turn.MaxStatsRevised[command.TrainIndex - 1] - currentStat;
                    table.AddRow(I18N_CurrentRemainStat);
                    table.AddRow($"{currentStat}:{statUpToMax switch
                    {
                        > 400 => $"{statUpToMax}",
                        > 200 => $"[yellow]{statUpToMax}[/]",
                        _ => $"[red]{statUpToMax}[/]"
                    }}");
                    table.AddRow(new Rule());

                    var afterVital = trainStats[command.TrainIndex - 1].VitalGain + turn.Vital;
                    table.AddRow(afterVital switch
                    {
                        < 30 => $"{I18N_Vital}:[red]{afterVital}[/]/{turn.MaxVital}",
                        < 50 => $"{I18N_Vital}:[darkorange]{afterVital}[/]/{turn.MaxVital}",
                        < 70 => $"{I18N_Vital}:[yellow]{afterVital}[/]/{turn.MaxVital}",
                        _ => $"{I18N_Vital}:[green]{afterVital}[/]/{turn.MaxVital}"
                    });

                    // 不同训练挖掘量
                    var gain = 0;
                    if (dataset != null && command.TrainIndex > 0 &&
                        dataset.command_info_array.Length > command.TrainLevel - 1)
                    {
                        var dig_array = dataset.command_info_array[command.TrainIndex - 1].dig_info_array;
                        if (dig_array.Length > 0)
                        {
                            gain = dig_array[0].dig_value;
                        }
                    }
                    table.AddRow($"Lv{command.TrainLevel} | 挖: {gain}");
                    table.AddRow(new Rule());

                    var stats = trainStats[command.TrainIndex - 1];
                    var score = stats.FiveValueGain.Sum();
                    if (score == trainStats.Max(x => x.FiveValueGain.Sum()))
                        table.AddRow($"{I18N_StatSimple}:[aqua]{score}[/]|Pt:{stats.PtGain}");
                    else
                        table.AddRow($"{I18N_StatSimple}:{score}|Pt:{stats.PtGain}");

                    foreach (var trainingPartner in command.TrainingPartners)
                    {
                        table.AddRow(trainingPartner.Name);
                        if (trainingPartner.Shining)
                            table.BorderColor(Color.LightGreen);
                    }
                    for (var i = 5 - command.TrainingPartners.Count(); i > 0; i--)
                    {
                        table.AddRow(string.Empty);
                    }
                    table.AddRow(new Rule());

                    return new Padder(table).Padding(0, 0, 0, 0);
                }).ToList();
                grids.AddRow([.. commands]);

                layout["训练信息"].Update(grids);
            }
            else
            {
                var grids = new Grid();
                grids.AddColumns(1);
                grids.AddRow([$"非训练阶段，stage={stage}"]);
                layout["训练信息"].Update(grids);
                noTrainingTable = true;
            }

            // 计算挖掘进度
            var onsen_info = dataset.onsen_info_array.First(x => x.state == 2);
            if (onsen_info != null) {
                var rest = 0;
                foreach (var layer in onsen_info.stratum_info_array)
                {
                    rest += layer.rest_volume;
                }
                layout["挖掘进度"].Update(new Panel($"挖掘进度剩余: {rest}").Expand());
            }

            // 额外信息
            var exTable = new Table().AddColumn("Extras");
            exTable.HideHeaders();
            // 挖掘力
            if (dataset != null && dataset.dig_effect_info_array.Length >= 3) 
            {
                exTable.AddRow(new Markup("挖掘力加成"));
                for (var i = 0; i < 3; i++)
                {
                    var value = dataset.dig_effect_info_array[i];
                    string[] toolNames = { "砂", "土", "岩" };
                    exTable.AddRow(new Markup($"{toolNames[i]} Lv {value.item_level} +{value.dig_effect_value}%"));
                }
            }
            // 体力消耗（测试）
            if (EventLogger.vitalSpent > 0)
            {
                exTable.AddRow(new Markup($"[blue]累计体力消耗: {EventLogger.vitalSpent}[/]"));
            }
            // 计算连续事件表现
            var eventPerf = EventLogger.PrintCardEventPerf(@event.data.chara_info.scenario_id);
            if (eventPerf.Count > 0)
            {
                exTable.AddRow(new Rule());
                foreach (var row in eventPerf)
                    exTable.AddRow(new Markup(row));
            }

            layout["日期"].Update(new Panel($"{turn.Year}{I18N_Year} {turn.Month}{I18N_Month}{turn.HalfMonth}").Expand());
            layout["总属性"].Update(new Panel($"[cyan]总属性: {totalValue}, Pt: {@event.data.chara_info.skill_point}[/]").Expand());
            layout["体力"].Update(new Panel($"{I18N_Vital}: [green]{turn.Vital}[/]/{turn.MaxVital}").Expand());
            layout["干劲"].Update(new Panel(@event.data.chara_info.motivation switch
            {
                // 换行分裂和箭头符号有关，去掉
                5 => $"[green]{I18N_MotivationBest}[/]",
                4 => $"[yellow]{I18N_MotivationGood}[/]",
                3 => $"[red]{I18N_MotivationNormal}[/]",
                2 => $"[red]{I18N_MotivationBad}[/]",
                1 => $"[red]{I18N_MotivationWorst}[/]"
            }).Expand());

            var availableTrainingCount = @event.data.home_info.command_info_array.Count(x => x.is_enable == 1);
            if (availableTrainingCount <= 1)
            {
                critInfos.Add("[aqua]非训练回合[/]");
            }
            if (@event.data.chara_info.skill_point > 9500)
            {
                critInfos.Add("[red]剩余PT>9500（上限9999），请及时学习技能");
            }
            layout["重要信息"].Update(new Panel(string.Join(Environment.NewLine, critInfos)).Expand());

            layout["Ext"].Update(exTable);

            GameStats.Print();

            AnsiConsole.Write(layout);
            // 光标倒转一点
            if (noTrainingTable)
                AnsiConsole.Cursor.SetPosition(0, 15);
            else
                AnsiConsole.Cursor.SetPosition(0, 31);
        }
    }
}
