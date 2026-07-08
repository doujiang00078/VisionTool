using HalconDotNet;
using System;
using System.Collections.Generic;
using VisionTool.Enums;

namespace VisionTool.Models
{
    [Serializable]
    public class GlobalParameter : IDisposable
    {
        /// <summary>
        /// 配方类型
        /// </summary>
        public FormulaType FormulaType { get; set; }

        [Newtonsoft.Json.JsonIgnore]
        private HObject _modeImage;

        /// <summary>
        /// 模板图像
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public HObject ModelImage
        {
            get { return _modeImage; }
            set
            {
                _modeImage?.Dispose();
                _modeImage = value;
            }
        }

        /// <summary>
        /// 全局参数列表
        /// </summary>
        public Dictionary<string, BaseParam> BaseParams { get; set; }

        public void Dispose()
        {
            _modeImage?.Dispose();
            _modeImage = null;
        }
    }
}