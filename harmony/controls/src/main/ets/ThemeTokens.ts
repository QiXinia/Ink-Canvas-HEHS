// @parity src/InkCanvas.Controls/Themes/Tokens.xaml
// 数值来自 parity/tokens.json 与 05-controls 研究；浮条按 S=50/36 换算。
export interface ThemeTokens {
  surface: string;
  surfaceAlt: string;
  stroke: string;
  textPrimary: string;
  textSecondary: string;
  accent: string;
  accentSoft: string;
  boardBlack: string;
  boardWhite: string;
  floatBarBackground: string;
  floatBarBorder: string;
  floatBarSelected: string;
  boardBarDark: string;
  badgeBackground: string;
  touchTarget: number;
  iconSize: number;
  labelSize: number;
  buttonWidth: number;
  buttonHeight: number;
  buttonRadius: number;
  selectedRadius: number;
  barCorner: number;
  controlCorner: number;
  barPadding: number[];
  cardPadding: number[];
}

const LIGHT: ThemeTokens = {
  surface: '#FFF6F7F9',
  surfaceAlt: '#FFFFFFFF',
  stroke: '#1F000000',
  textPrimary: '#E4000000',
  textSecondary: '#9E000000',
  accent: '#FF0067C0',
  accentSoft: '#1A0067C0',
  boardBlack: '#FF1F3B2C',
  boardWhite: '#FFFAFAFA',
  floatBarBackground: '#BFFFFFFF',
  floatBarBorder: '#CC0066BF',
  floatBarSelected: '#801296DB',
  boardBarDark: '#BF000000',
  badgeBackground: '#E60066BF',
  touchTarget: 44,
  iconSize: 22.222,
  labelSize: 11.111,
  buttonWidth: 41.667,
  buttonHeight: 44.444,
  buttonRadius: 6.944,
  selectedRadius: 5.556,
  barCorner: 14,
  controlCorner: 8,
  barPadding: [8, 6, 8, 6],
  cardPadding: [16, 12, 16, 12]
};

const DARK: ThemeTokens = {
  surface: '#FF202124',
  surfaceAlt: '#FF2B2C2F',
  stroke: '#24FFFFFF',
  textPrimary: '#F2FFFFFF',
  textSecondary: '#A8FFFFFF',
  accent: '#FF4CC2FF',
  accentSoft: '#264CC2FF',
  boardBlack: '#FF16281E',
  boardWhite: '#FF1B1B1B',
  floatBarBackground: '#99000000',
  floatBarBorder: '#E6FFFFFF',
  floatBarSelected: '#801296DB',
  boardBarDark: '#BF000000',
  badgeBackground: '#E61E3A8A',
  touchTarget: 44,
  iconSize: 22.222,
  labelSize: 11.111,
  buttonWidth: 41.667,
  buttonHeight: 44.444,
  buttonRadius: 6.944,
  selectedRadius: 5.556,
  barCorner: 14,
  controlCorner: 8,
  barPadding: [8, 6, 8, 6],
  cardPadding: [16, 12, 16, 12]
};

export function themeTokens(dark: boolean): ThemeTokens {
  return dark ? DARK : LIGHT;
}
