using System.Windows;
using VisionTool.Models;

namespace VisionTool.UI
{
    public partial class SampleWindow : Window
    {
        public SampleWindow()
        {
            InitializeComponent();

            var engine = new ConfigEngineViewModel();

            // 示例配置对象
            var edgeConfig = new EdgeConfig { Threshold = 50, Enabled = true };
            engine.AddModule("Edge Detection", edgeConfig,
                ("Apply", (EdgeConfig c) => MessageBox.Show($"Apply clicked: Threshold={c.Threshold}")),
                ("Reset", (EdgeConfig c) => { c.Threshold = 10; c.Enabled = false; MessageBox.Show("Reset applied"); })
            );

            var colorConfig = new ColorConfig { Hue = 180, Saturation = 100 };
            engine.AddModule("Color", colorConfig,
                ("Auto", (ColorConfig c) => MessageBox.Show($"Auto clicked: Hue={c.Hue}"))
            );

            engine.AddGlobalButton("Save All", () => MessageBox.Show("Save All clicked"));
            engine.AddGlobalButton("Load", () => MessageBox.Show("Load clicked"));

            DynamicView.SetConfigEngine(engine);
        }

    }
    public class EdgeConfig : BaseParam
    {
        public int Threshold { get; set; } = 20;
        public bool Enabled { get; set; } = true;
    }
    public class ColorConfig : BaseParam
    {
        public int Hue { get; set; } = 0;
        public int Saturation { get; set; } = 0;
    }
}
