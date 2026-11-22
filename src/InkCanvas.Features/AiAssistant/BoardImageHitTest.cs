using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;

namespace InkCanvas.Features.AiAssistant;

/// <summary>
/// 在世界坐标（Canvas DIP）中命中当前页图片。使用旋转/翻转后的外接矩形
/// （<see cref="BoardService.TransformedBounds"/>），并按绘制顺序从后往前查找。
/// 调用方需先把 view 坐标反变换为世界坐标。
/// </summary>
public static class BoardImageHitTest
{
    public static BoardImageAsset HitTest(IReadOnlyList<BoardImageAsset> images, float worldX, float worldY)
    {
        if (images is null) return null;

        for (int index = images.Count - 1; index >= 0; index--)
        {
            BoardImageAsset image = images[index];
            if (image is null || image.Width <= 0 || image.Height <= 0) continue;

            Rect bounds = BoardElementMath.TransformedBounds(
                image.Left, image.Top, image.Width, image.Height, image.Transform);
            if (worldX >= bounds.Left && worldX <= bounds.Right
                && worldY >= bounds.Top && worldY <= bounds.Bottom)
                return image;
        }

        return null;
    }
}