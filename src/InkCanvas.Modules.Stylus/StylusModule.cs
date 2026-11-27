using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.Stylus;

public sealed class StylusModule : IApplicationModule
{
    private StylusService _service;
    public void Start(IModuleContext context)
    {
        _service = new(context.GetService<IStylusInputHost>());
        context.RegisterCleanup(_service.Dispose);
        context.Export(_service);
        context.RegisterCleanup(context.GetService<IModuleSettingsRegistry>().Register(new(
            "inkcanvas.stylus.settings", "手写笔与数位板", "\uF3DD",
            "侧键 按钮 笔尾 橡皮 倒置 数位板 Windows Ink HID 快捷键",
            builder =>
            {
                builder.AddToggle("启用笔与数位板按钮映射", () => _service.Settings.Enabled,
                    value => { _service.Settings.Enabled = value; _service.Configure(); });
                builder.AddInfo("为笔侧键和数位板按钮选择工具或操作。临时切换在松开后恢复，固定切换保持工具，再次按下还原会在两种工具间切换。书写中改变按钮时在抬笔后应用。进入其他窗口或切页后会释放临时工具。");
                builder.AddView("按钮映射", () => new StylusSettingsView(_service), "侧键 数位板 按键学习");
                builder.AddStatus("按钮接收状态", () => _service.Status);
                builder.AddInfo("Windows Ink 主侧键和倒置笔尾直接识别。第二侧键需要驱动报告独立按钮；数位板可学习标准 HID 按钮，或学习驱动设置的键盘快捷键、鼠标中键和扩展键。厂商专用按钮请先在设备驱动中设置为快捷键，再点击学习按钮。映射仅在本应用画布可操作时生效。");
            })).Dispose);
    }
    public void Stop() { _service?.Dispose(); _service = null; }
}
