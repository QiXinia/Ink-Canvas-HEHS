// @parity src/InkCanvas.Features/Persistence/Stores.cs (SettingsStore) + src/InkCanvas.Core/Settings/AppSettings.cs
// Settings.json 编解码：大小写不敏感读取、88 个已知字段、未知字段丢弃（与 C# JsonSerializer 行为一致）、
// 枚举保持数字、缺失字段回落构造默认值。
import { createDefaultSettings } from '../settings/AppSettings';
import type {
  AdvancedSettings,
  ExperimentalSettings,
  AppearanceSettings,
  AppSettings,
  AutomationSettings,
  CanvasSettings,
  GestureSettings,
  InkToShapeSettings,
  PowerPointSettings,
  RandSettings,
  StartupSettings
} from '../settings/AppSettings';

const BOOL = 0;
const INT = 1;
const NUM = 2;
const STR = 3;
// 可空数值：读取接受 number/null（缺失/非法回落 null）；写出时 null 省略
// （对应 C# JsonIgnore(WhenWritingNull)，如 highlighterWidth）。
const NUM_NULL = 4;

class FieldDef {
  key: string;
  kind: number;

  constructor(key: string, kind: number) {
    this.key = key;
    this.kind = kind;
  }
}

class SectionDef {
  key: string;
  fields: FieldDef[];

  constructor(key: string, fields: FieldDef[]) {
    this.key = key;
    this.fields = fields;
  }
}

const DEF_ADVANCED = new SectionDef('advanced', [
  new FieldDef('isSpecialScreen', BOOL),
  new FieldDef('isQuadIR', BOOL),
  new FieldDef('touchMultiplier', NUM),
  new FieldDef('nibModeBoundsWidth', INT),
  new FieldDef('fingerModeBoundsWidth', INT),
  new FieldDef('nibModeBoundsWidthThresholdValue', NUM),
  new FieldDef('fingerModeBoundsWidthThresholdValue', NUM),
  new FieldDef('nibModeBoundsWidthEraserSize', NUM),
  new FieldDef('fingerModeBoundsWidthEraserSize', NUM),
  new FieldDef('isEnableEdgeGestureUtil', BOOL),
  new FieldDef('isLogEnabled', BOOL),
  new FieldDef('isSecondConfimeWhenShutdownApp', BOOL)
]);

const DEF_APPEARANCE = new SectionDef('appearance', [
  new FieldDef('isEnableDisPlayFloatBarText', BOOL),
  new FieldDef('isEnableDisPlayNibModeToggler', BOOL),
  new FieldDef('isColorfulViewboxFloatingBar', BOOL),
  new FieldDef('enableViewboxFloatingBarScaleTransform', NUM),
  new FieldDef('enableViewboxBlackBoardScaleTransform', NUM),
  new FieldDef('floatingBarBottomMargin', NUM),
  new FieldDef('isTransparentButtonBackground', BOOL),
  new FieldDef('isShowExitButton', BOOL),
  new FieldDef('isShowEraserButton', BOOL),
  new FieldDef('isShowHideControlButton', BOOL),
  new FieldDef('isShowLRSwitchButton', BOOL),
  new FieldDef('isShowModeFingerToggleSwitch', BOOL),
  new FieldDef('theme', INT)
]);

const DEF_AUTOMATION = new SectionDef('automation', [
  new FieldDef('isAutoFoldInEasiNote', BOOL),
  new FieldDef('isAutoFoldInEasiNoteIgnoreDesktopAnno', BOOL),
  new FieldDef('isAutoFoldInEasiCamera', BOOL),
  new FieldDef('isAutoFoldInEasiNote3C', BOOL),
  new FieldDef('isAutoFoldInSeewoPincoTeacher', BOOL),
  new FieldDef('isAutoFoldInHiteTouchPro', BOOL),
  new FieldDef('isAutoFoldInHiteCamera', BOOL),
  new FieldDef('isAutoFoldInWxBoardMain', BOOL),
  new FieldDef('isAutoFoldInOldZyBoard', BOOL),
  new FieldDef('isAutoFoldInMSWhiteboard', BOOL),
  new FieldDef('isAutoFoldInPPTSlideShow', BOOL),
  new FieldDef('isAutoKillPptService', BOOL),
  new FieldDef('isAutoKillEasiNote', BOOL),
  new FieldDef('isSaveScreenshotsInDateFolders', BOOL),
  new FieldDef('isAutoSaveStrokesAtScreenshot', BOOL),
  new FieldDef('isAutoSaveStrokesAtClear', BOOL),
  new FieldDef('isAutoClearWhenExitingWritingMode', BOOL),
  new FieldDef('minimumAutomationStrokeNumber', INT),
  new FieldDef('autoSavedStrokesLocation', STR),
  new FieldDef('autoDelSavedFiles', BOOL),
  new FieldDef('autoDelSavedFilesDaysThreshold', INT)
]);

const DEF_BEHAVIOR = new SectionDef('behavior', [
  new FieldDef('isShowPPTNavigationBottom', BOOL),
  new FieldDef('isShowPPTNavigationSides', BOOL),
  new FieldDef('isShowBottomPPTNavigationPanel', BOOL),
  new FieldDef('isShowSidePPTNavigationPanel', BOOL),
  new FieldDef('powerPointSupport', BOOL),
  new FieldDef('isShowCanvasAtNewSlideShow', BOOL),
  new FieldDef('isNoClearStrokeOnSelectWhenInPowerPoint', BOOL),
  new FieldDef('isShowStrokeOnSelectInPowerPoint', BOOL),
  new FieldDef('isAutoSaveStrokesInPowerPoint', BOOL),
  new FieldDef('isAutoSaveScreenShotInPowerPoint', BOOL),
  new FieldDef('isNotifyPreviousPage', BOOL),
  new FieldDef('isNotifyHiddenPage', BOOL),
  new FieldDef('isNotifyAutoPlayPresentation', BOOL),
  new FieldDef('isEnableTwoFingerGestureInPresentationMode', BOOL),
  new FieldDef('isEnableFingerGestureSlideShowControl', BOOL),
  new FieldDef('isSupportWPS', BOOL)
]);

const DEF_CANVAS = new SectionDef('canvas', [
  new FieldDef('inkWidth', NUM),
  new FieldDef('highlighterWidth', NUM_NULL),
  new FieldDef('inkAlpha', INT),
  new FieldDef('isCompressPicturesUploaded', BOOL),
  new FieldDef('isShowCursor', BOOL),
  new FieldDef('inkStyle', INT),
  new FieldDef('eraserSize', INT),
  new FieldDef('eraserType', INT),
  new FieldDef('hideStrokeWhenSelecting', BOOL),
  new FieldDef('usingWhiteboard', BOOL),
  new FieldDef('penNib', INT)
]);

const DEF_GESTURE = new SectionDef('gesture', [
  new FieldDef('isEnableMultiTouchMode', BOOL),
  new FieldDef('isEnableTwoFingerZoom', BOOL),
  new FieldDef('isEnableTwoFingerTranslate', BOOL),
  new FieldDef('AutoSwitchTwoFingerGesture', BOOL),
  new FieldDef('isEnableTwoFingerRotation', BOOL),
  new FieldDef('isEnableTwoFingerRotationOnSelection', BOOL),
  new FieldDef('matrixTransformCenterPoint', INT),
  new FieldDef('isEnableStylusGesture', BOOL)
]);

const DEF_INK_TO_SHAPE = new SectionDef('inkToShape', [
  new FieldDef('isInkToShapeEnabled', BOOL)
]);

const DEF_STARTUP = new SectionDef('startup', [
  new FieldDef('isAutoUpdate', BOOL),
  new FieldDef('isAutoUpdateWithSilence', BOOL),
  new FieldDef('isAutoUpdateWithSilenceStartTime', STR),
  new FieldDef('isAutoUpdateWithSilenceEndTime', STR),
  new FieldDef('isEnableNibMode', BOOL),
  new FieldDef('isFoldAtStartup', BOOL)
]);

const DEF_RAND = new SectionDef('randSettings', [
  new FieldDef('peopleCount', INT),
  new FieldDef('isNotRepeatName', BOOL)
]);

// 基线演进：experimental 组（ScreenTracking）；键名 PascalCase（C# 默认序列化名，读取大小写不敏感）。
const DEF_EXPERIMENTAL = new SectionDef('experimental', [
  new FieldDef('ScreenContentTracking', BOOL),
  new FieldDef('FollowScroll', BOOL),
  new FieldDef('FollowZoom', BOOL),
  new FieldDef('MaskFixedRegions', BOOL),
  new FieldDef('RememberPages', BOOL),
  new FieldDef('PreserveUnchangedRegions', BOOL),
  new FieldDef('Word', BOOL),
  new FieldDef('PowerPoint', BOOL),
  new FieldDef('Edge', BOOL)
]);

function asDict(value: Object): Record<string, Object> {
  return value as Record<string, Object>;
}

function getField(obj: Object, key: string): Object | null {
  const dict = asDict(obj);
  const keys = Object.keys(dict);
  const lower = key.toLowerCase();
  for (let i = 0; i < keys.length; i++) {
    if (keys[i].toLowerCase() === lower) {
      return dict[keys[i]];
    }
  }
  return null;
}

function coerce(value: Object, kind: number, fallback: Object): Object {
  if (kind === BOOL) {
    return typeof value === 'boolean' ? value : fallback;
  }
  if (kind === NUM_NULL) {
    if (value === null) {
      return null;
    }
    const nullable = Number(value);
    return isFinite(nullable) ? nullable : fallback;
  }
  if (kind === INT || kind === NUM) {
    const n = Number(value);
    if (!isFinite(n)) {
      return fallback;
    }
    return kind === INT ? Math.trunc(n) : n;
  }
  return typeof value === 'string' ? value : fallback;
}

function readSection(root: Object, def: SectionDef, defaults: Object): Object {
  const out: Record<string, Object> = {};
  const defDict = asDict(defaults);
  const defKeys = Object.keys(defDict);
  for (let i = 0; i < defKeys.length; i++) {
    out[defKeys[i]] = defDict[defKeys[i]];
  }
  const raw = getField(root, def.key);
  if (raw !== null && typeof raw === 'object' && !Array.isArray(raw)) {
    for (let i = 0; i < def.fields.length; i++) {
      const field = def.fields[i];
      const value = getField(raw, field.key);
      if (value !== null) {
        out[field.key] = coerce(value, field.kind, out[field.key]);
      }
    }
  }
  // 可空字段为 null 时键不存在（对应 C# JsonIgnore(WhenWritingNull) 的序列化形态）。
  for (let i = 0; i < def.fields.length; i++) {
    const field = def.fields[i];
    if (field.kind === NUM_NULL && out[field.key] === null) {
      delete out[field.key];
    }
  }
  return out;
}

function pickSection(section: Object, def: SectionDef): Object {
  const src = asDict(section);
  const dst: Record<string, Object> = {};
  for (let i = 0; i < def.fields.length; i++) {
    const field = def.fields[i];
    const value = src[field.key];
    if (field.kind === NUM_NULL) {
      if (value !== null && value !== undefined) {
        dst[field.key] = value;
      }
      continue;
    }
    dst[field.key] = value;
  }
  return dst;
}

export function parseSettings(json: string): AppSettings {
  let root: Object | null = null;
  try {
    root = JSON.parse(json) as Object;
  } catch (e) {
    return createDefaultSettings();
  }
  if (root === null || typeof root !== 'object' || Array.isArray(root)) {
    return createDefaultSettings();
  }
  const result = createDefaultSettings();
  result.advanced = readSection(root, DEF_ADVANCED, result.advanced) as AdvancedSettings;
  result.appearance = readSection(root, DEF_APPEARANCE, result.appearance) as AppearanceSettings;
  result.automation = readSection(root, DEF_AUTOMATION, result.automation) as AutomationSettings;
  result.behavior = readSection(root, DEF_BEHAVIOR, result.behavior) as PowerPointSettings;
  result.canvas = readSection(root, DEF_CANVAS, result.canvas) as CanvasSettings;
  result.gesture = readSection(root, DEF_GESTURE, result.gesture) as GestureSettings;
  result.inkToShape = readSection(root, DEF_INK_TO_SHAPE, result.inkToShape) as InkToShapeSettings;
  result.startup = readSection(root, DEF_STARTUP, result.startup) as StartupSettings;
  result.randSettings = readSection(root, DEF_RAND, result.randSettings) as RandSettings;
  result.experimental = readSection(root, DEF_EXPERIMENTAL, result.experimental) as ExperimentalSettings;
  return result;
}

export function stringifySettings(settings: AppSettings): string {
  const out: Record<string, Object> = {};
  out['advanced'] = pickSection(settings.advanced as Object, DEF_ADVANCED);
  out['appearance'] = pickSection(settings.appearance as Object, DEF_APPEARANCE);
  out['automation'] = pickSection(settings.automation as Object, DEF_AUTOMATION);
  out['behavior'] = pickSection(settings.behavior as Object, DEF_BEHAVIOR);
  out['canvas'] = pickSection(settings.canvas as Object, DEF_CANVAS);
  out['gesture'] = pickSection(settings.gesture as Object, DEF_GESTURE);
  out['inkToShape'] = pickSection(settings.inkToShape as Object, DEF_INK_TO_SHAPE);
  out['startup'] = pickSection(settings.startup as Object, DEF_STARTUP);
  out['randSettings'] = pickSection(settings.randSettings as Object, DEF_RAND);
  out['experimental'] = pickSection(settings.experimental as Object, DEF_EXPERIMENTAL);
  return JSON.stringify(out);
}

export function settingsRoundtrip(json: string): AppSettings {
  return parseSettings(stringifySettings(parseSettings(json)));
}
