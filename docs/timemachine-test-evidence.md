# 板书时光机验收测试证据（本轮）

- 生成日期：2026-11-21
- 环境：`DOTNET_ROOT=D:\dotnet`、`PATH` 含 `D:\dotnet`、`NUGET_PACKAGES=D:\dev-cache\nuget`；执行前结束运行中的 `InkCanvas.exe`
- 分类命令：
  ```
  dotnet test tests/InkCanvas.Classroom.Tests/InkCanvas.Classroom.Tests.csproj -c Debug -p:Platform=x64 \
    --filter "FullyQualifiedName~EndToEndReplay|FullyQualifiedName~TimeMachineAdvanced|FullyQualifiedName~RecordingEnhancement|FullyQualifiedName~TimelineReliability|FullyQualifiedName~TimeMachineTests|FullyQualifiedName~TimeAxisTests" \
    --logger "console;verbosity=detailed"
  ```
- 结果：**测试总数 91，通过 91，失败 0**（输出原文留存：`D:\hw\artifacts\test-evidence-raw.txt`）

## 验收第 2 条：跨页 / 背景 / 擦除 / 撤销 / 清空 / 课件批注按原顺序回放

| 用例 | 结果 |
| --- | --- |
| `EndToEndReplayTests.SyntheticSession_CrossPageBackgroundEraseClearMatchesEventOrder` | 通过 |
| `EndToEndReplayTests.UndoRedo_RestoresStrokeInFrameOrder` | 通过 |
| `EndToEndReplayTests.ClearedSecondPage_RemovesOnlyItsStrokesWhenReturning` | 通过 |
| `EndToEndReplayTests.PptSlidePayload_EncodesSlideIdIndexAndEmptyScreenshot` | 通过 |
| `EndToEndReplayTests.TimelineIndexAt_IsChronologicalAndMonotonic` | 通过 |
| `TimeMachineTests.BuildFrame_MultiPageFiltersByCurrentPage` | 通过 |
| `TimeMachineTests.BuildFrame_ClearEmptiesPage` | 通过 |
| `TimeMachineTests.BuildFrame_RemovesStrokes` | 通过 |
| `TimeMachineTests.BuildFrame_UndoRedoAppliesDiff` | 通过 |
| `TimeMachineTests.BuildFrame_UndoRedo_ReAddsStrokeWithEmbeddedJson` | 通过 |
| `TimeMachineTests.BackgroundAt_ChangesOverTime` | 通过 |
| `TimeAxisTests.SceneAt_RestoresPageAndBackgroundAtAnyTime` | 通过 |
| `TimeAxisTests.SceneAt_RestoresPptSlideRosterAndTimer` | 通过 |

## 验收第 3 条：同一时间点路径一致；0.5x–8x 切换无跳笔重笔

| 用例 | 结果 |
| --- | --- |
| `EndToEndReplayTests.RepeatedBuildFrame_IsBitIdenticalAcrossFivePasses` | 通过（多时间点指纹一致） |
| `EndToEndReplayTests.PlaybackSpeed_ArrivalFrameMatchesDirectSeek` | 通过（0.5/1/2/8x 到达帧 == 直接定位） |
| `EndToEndReplayTests.SpeedSwitching_KeepsIndexAtMonotonicAndFramesStable` | 通过（倍速切换事件索引单调） |
| `EndToEndReplayTests.RandomSeek_Ascending_MatchesSequentialPlayback` | 通过 |
| `EndToEndReplayTests.RandomSeek_Descending_MatchesSequentialPlayback` | 通过（倒退后重放） |
| `EndToEndReplayTests.RandomSeek_Shuffled_MatchesSequentialPlayback` | 通过 |
| `TimeMachineTests.ReplayClock_SpeedScaling_AdvancesElapsed` | 通过 |
| `TimeMachineTests.BuildFrame_AfterSaveLoad_MatchesOriginal` | 通过 |
| `TimelineReliabilityTests.SameMillisecondEvents_AreOrderedBySeq_NotInsertionOrder` | 通过 |
| `TimelineReliabilityTests.BuildFrame_DoesNotMutateEventStream` | 通过（回放只读） |

## 验收第 4 条：异常恢复与暂停语义

| 用例 | 结果 |
| --- | --- |
| `RecordingThroughputTests.JournalAndStore_90Minutes_54000Events_WritesAndRecovers` | 通过（54,000 事件写入 191ms，恢复事件数一致） |
| `BatchCTests.Journal_TruncatedLastLine_RecoversWithoutThrowing` | 通过（强杀截断半行容错） |
| `BatchCTests.Journal_RecoveredSessionMatchesFullSave` | 通过 |
| `BatchCTests.Journal_MergesWithSnapshot_AndPrefersLongerEventStream` | 通过 |
| `BatchCTests.Journal_AppendAndRecover_ReturnsSameEvents` | 通过 |
| `ExportSyncTests.JournalRecovery_LastEventTime_IsWithinTwoSeconds_AndJournalRetained` | 通过（最多丢约 2 秒） |
| `RecordingEnhancementTests.PendingJournalIds_DetectsUnfinishedRecording` | 通过（启动检测未完成会话） |
| `RecordingEnhancementTests.Resume_ElapsedExcludesPausedDuration` | 通过 |
| `RecordingEnhancementTests.Elapsed_WhilePaused_StaysAtPauseBoundary` | 通过 |
| `RecordingEnhancementTests.RecordedTimestamps_RemainMonotonicAcrossPauseResume` | 通过 |
| `RecordingEnhancementTests.Pause_DiscardsAutomaticViewEventsAndResumeRestoresThem` | 通过 |
| `LongPlaybackStressTests.BuildFrame_90MinutePlayback_30Fps_KeepsTimeAndMemoryBounded` | 通过（162,001 帧 / 106.96s / 内存 55.6→57.1MB） |

## 验收第 5 条：定位耗时与过期任务取消

| 用例 | 结果 |
| --- | --- |
| `TimelineReliabilityTests.NinetyMinuteSession_SeekIsWithinOneSecond` | 通过（冷启动 153ms，10 次定位 13ms） |
| `TimeMachineTests.Checkpoint_GeneratedByCount_AndNearestIsCorrect` | 通过（检查点定位） |
| `TimeMachineTests.BuildFrame_CheckpointIncrementalMatchesExpected` | 通过（检查点增量 == 全量） |
| `TimelineReliabilityTests.Checkpoints_CarrySeq_ForStableRelocation` | 通过 |
| `TimeMachineAdvancedTests.SeekGate_CancelledTokenOrStaleVersion_DoesNotApply` | 通过（过期定位不应用） |
| `TimeMachineAdvancedTests.StepTarget_ClampsAtBothEnds` | 通过（逐事件边界） |

## 验收第 6 条：导出区间 / 倍速 / 仅板书 / 并发

| 用例 | 结果 |
| --- | --- |
| `EndToEndReplayTests.ExportSampling_At1x_MatchesBuildFrameFingerprint` | 通过（51 帧指纹一致） |
| `EndToEndReplayTests.ExportSampling_At2x_MatchesBuildFrameFingerprint` | 通过（26 帧指纹一致） |
| `EndToEndReplayTests.BoardOnlyExport_UsesSameStrokeSetAsFullClassroom` | 通过 |
| `EndToEndReplayTests.ExportWhileWriting_MeetsStrokeLatencyBudget` | 通过（并发书写不阻塞） |
| `TimeMachineAdvancedTests.SampleFrameTimes_1x_UsesRealTimeAndFullDuration` | 通过 |
| `TimeMachineAdvancedTests.SampleFrameTimes_2x_HalvesOutputDuration` | 通过 |
| `TimeMachineAdvancedTests.SampleFrameTimes_4x_QuartersOutputDuration` | 通过 |
| `TimeMachineAdvancedTests.SampleFrameTimes_KeepsStartOffsetAndStaysWithinRange` | 通过（区间片段起点） |
| `TimeMachineAdvancedTests.EnsureFreeDiskSpace_InsufficientSpace_ThrowsChineseMessage` | 通过 |
| `ExportTimingTests.Export120SecondSyntheticSession_MeasuresRealMp4Timing` | 通过（3,600 帧 / 10.3s / 134,900 字节） |

音画同步与恢复窗口实测输出（`artifacts/export-sync-raw.txt`）：

```
录制 1000 ms → 音频 500.0 ms / 24000 帧，视频 15 帧 = 500.0 ms，偏差 0.00 ms（1 帧 = 33.3 ms）；MP4 轨道：视频 500.0 ms，音频 512.0 ms。
逻辑末事件 5000 ms，journal 末事件 4000 ms，恢复末事件 4000 ms，崩溃丢帧窗口 1000 ms（上限 2000 ms）。
无音频设备静默降级：未找到可用的系统音频输出设备，无法录制系统声音。已仅导出视频。
完整 60 帧；区间 [900,1700] 24 帧；2× 区间 12 帧；首帧笔画 1 个。
```

口径：上述音轨为测试内合成的 WAV，经真实 Media Foundation 编码进 MP4（音频轨 512ms）；**真实麦克风/课堂讲解音频未验证**。

## 验收第 7 条：旧版迁移 / 校验 / 回归

| 用例 | 结果 |
| --- | --- |
| `TimelineReliabilityTests.LegacySessionWithoutVersion_GetsBackupAndUpgradedFields` | 通过（旧版写 `.bak` + Version=2 + Bookmarks） |
| `TimeAxisTests.LegacyV1Session_WithoutVersionOrBookmarks_Loads` | 通过 |
| `TimelineReliabilityTests.Save_WritesChecksumAndLoadVerifiesIt` | 通过 |
| `TimelineReliabilityTests.TamperedFile_WithoutBackup_IsRejected` | 通过 |
| `TimelineReliabilityTests.TamperedFile_WithBackup_FallsBackToBackup` | 通过 |
| `TimeAxisTests.SessionV2_SaveLoad_RoundTripsBookmarks` | 通过 |
| `TimeAxisTests.SessionV2_JsonUsesCamelCaseKeys` | 通过 |
| `BatchCTests.Journal_OldSessionWithoutJournal_LoadsNormally` | 通过 |

## 全量回归计数（同期）

| 项目 | 结果 |
| --- | --- |
| `InkCanvas.Classroom.Tests` | 通过 269 / 269 |
| `InkCanvas.Ai.Tests` | 通过 132 / 132 |
| `InkCanvas.P0.Tests` | 通过 33 / 33 |
| `InkCanvas.Core.Tests` | 通过 148 / 148 |
| 构建 `InkCanvas.App`（Debug x64） | 0 警告，0 错误 |

## 本证据不覆盖

- 真实 Win2D 栅格化、UI 线程消息循环与输入延迟；
- 200% 系统 DPI（需注销，未执行；注册表已恢复）；
- 真实课堂素材的 90 分钟录制；
- 真实音轨（WASAPI/AAC）与成片音画同步；本轮仅有合成音轨 MP4 实测（偏差 0.00ms）；
- WPS 放映与多显示器组合（本机仅 1 屏）。

## 实机补充取证（2026-11-21）

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 高对比度 | 黑底白字，AI 与图标清晰 | `artifacts/ai-icon-v2-bar-highcontrast.png` |
| 真实 PPT 放映 | p0-test.pptx 3 页，翻至 3/3，覆盖层与导航窗同步 | `artifacts/ppt-slideshow-overlay.png` |
| 暗色主题 | 深灰底浅色 AI | `artifacts/ai-icon-v2-bar-dark4.png` |
| 亮色主题 | 与改动前一致 | `artifacts/ai-icon-v2-bar-light-final2.png` |
| 125% 窗口缩放 | 浮条与 AI 按钮随比例放大、无截断 | `artifacts/ai-icon-v2-bar-scale125.png` || 200% 窗口缩放（设置值） | 实际渲染降级 150%，浮条 1287×105 完整居中、无裁切 | `artifacts/ai-icon-v2-fullscreen-scale200-fixed.png` |