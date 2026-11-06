using Warp.Tools;

namespace Refund.Utils;

public static class Class3DCardLayout
{
    // Bakery renders up to 20 classes: one row through five, then two rows
    // with 4, 6, 8 or 10 columns. Card dimensions describe the aspect ratio.
    public static int2 SquareCount(int classCount) => new(
        classCount <= 5 ? Math.Max(2, classCount) : Math.Min(5, (classCount + 3) / 4), 1);
}
