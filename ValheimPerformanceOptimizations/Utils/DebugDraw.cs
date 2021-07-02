using RuntimeDebugDraw;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    public class DebugDraw
    {
        private static void DrawBounds(Bounds b, float delay = 0)
        {
            // bottom
            var p1 = new Vector3(b.min.x, b.min.y, b.min.z);
            var p2 = new Vector3(b.max.x, b.min.y, b.min.z);
            var p3 = new Vector3(b.max.x, b.min.y, b.max.z);
            var p4 = new Vector3(b.min.x, b.min.y, b.max.z);

            Draw.DrawLine(p1, p2, Color.blue, delay);
            Draw.DrawLine(p2, p3, Color.red, delay);
            Draw.DrawLine(p3, p4, Color.yellow, delay);
            Draw.DrawLine(p4, p1, Color.magenta, delay);

            // top
            var p5 = new Vector3(b.min.x, b.max.y, b.min.z);
            var p6 = new Vector3(b.max.x, b.max.y, b.min.z);
            var p7 = new Vector3(b.max.x, b.max.y, b.max.z);
            var p8 = new Vector3(b.min.x, b.max.y, b.max.z);

            Draw.DrawLine(p5, p6, Color.blue, delay);
            Draw.DrawLine(p6, p7, Color.red, delay);
            Draw.DrawLine(p7, p8, Color.yellow, delay);
            Draw.DrawLine(p8, p5, Color.magenta, delay);

            // sides
            Draw.DrawLine(p1, p5, Color.white, delay);
            Draw.DrawLine(p2, p6, Color.gray, delay);
            Draw.DrawLine(p3, p7, Color.green, delay);
            Draw.DrawLine(p4, p8, Color.cyan, delay);
        }
    }
}