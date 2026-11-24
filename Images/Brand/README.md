# Ink Canvas 标志

标志由用户提供的《白板logo.svg》制作，原文件保存在 `Logo.Source.svg`。保留白板轮廓与中间笔画，去掉原白色复合背景，便于叠放到不同窗口材质。

- 浅色版采用深色轮廓 `#182F49` 与蓝色笔画 `#0067C0`。
- 深色版采用浅色轮廓 `#F2F6FC` 与亮蓝色笔画 `#60CDFF`。
- `InkCanvas.svg` 根据浏览器的系统深浅色偏好切换颜色，固定版用于印刷及原生界面。
- Windows EXE 和安装快捷方式采用浅底图块，便于在不同桌面背景中辨认。原生窗口使用对应主题的透明图标。

运行 `python tools/generate-brand-assets.py` 可以从保存的 SVG 重新生成 PNG 和 ICO，需要 Pillow、PyMuPDF。ICO 包含 16、20、24、32、40、48、64、128 与 256 像素图像。运行时无 Python 依赖。
