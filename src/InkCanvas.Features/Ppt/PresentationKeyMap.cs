namespace InkCanvas.Features.Ppt;

/// <summary>What a presentation key means to the shell.</summary>
public enum PresentationKeyAction { None, Next, Previous, ToggleBlackScreen }

/// <summary>
/// Pure mapping for presenter/clicker keys. Key handling stays in the shell: this table only
/// answers what a virtual key means while a slide show is running.
/// <para>
/// <c>B</c> is intentionally black-screen only (PowerPoint's own convention); the period,
/// Space and Page Down keys all advance, so a clicker that sends any of them keeps working.
/// </para>
/// </summary>
public static class PresentationKeyMap
{
    public const int VkBackspace = 0x08;
    public const int VkSpace = 0x20;
    public const int VkPageUp = 0x21;
    public const int VkPageDown = 0x22;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;
    public const int VkB = 0x42;
    public const int VkOemPeriod = 0xBE;
    public const int VkF9 = 0x78;

    public static PresentationKeyAction Map(int virtualKey) => virtualKey switch
    {
        VkPageDown or VkRight or VkDown or VkSpace or VkOemPeriod => PresentationKeyAction.Next,
        VkPageUp or VkLeft or VkUp or VkBackspace => PresentationKeyAction.Previous,
        VkB => PresentationKeyAction.ToggleBlackScreen,
        _ => PresentationKeyAction.None,
    };

    /// <summary>True when the shell should consume the key instead of passing it through.</summary>
    public static bool IsHandled(int virtualKey) => Map(virtualKey) != PresentationKeyAction.None;
}