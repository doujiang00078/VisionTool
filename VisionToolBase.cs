using Newtonsoft.Json.Linq;

namespace VisionTool
{
    public class VisionToolBase : BaseTool
    {
        protected ProgramType toolType;

        protected double _mmPerPixel = 0.015;

        public double MmPerPixel
        {
            get => _mmPerPixel;
            set => _mmPerPixel = value;
        }

        public ProgramType ToolType { get => toolType; set => toolType = value; }

        public VisionToolBase()
        {
        }

        public void InitVisionTool()
        {
        }

        public override void Backup()
        {
        }

        public override void RollBack()
        {
        }

        public override void Save(JObject json)
        {
            json["MmPerPixel"] = _mmPerPixel;
            json["ToolName"] = UserName;
        }

        public override void Load(JObject json)
        {
            _mmPerPixel = (double)json["MmPerPixel"];
            UserName = json["ToolName"].ToString();
        }

        public virtual void process()
        {
        }

        protected void initHalcon()
        {
            // LogHelperNS.LogHelper.LogMessage("设置内存：temporary_mem_cache, false", "检测线程", "info");
            //HOperatorSet.SetSystem("temporary_mem_cache", "false");
        }
    }
}