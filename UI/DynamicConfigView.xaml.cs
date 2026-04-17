using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using VisionTool.Interfaces;
using VisionTool.Models;

namespace VisionTool.UI
{
    public partial class DynamicConfigView : UserControl, IAlgorithmTaskView
    {

        // 外部可赋值的委托：将界面操作委托给外部处理者（例如 ViewModel 或控制器）
        // 当需要显示状态、显示图像或更新参数时，控件会调用这些委托
        public Action<string> OnSetStatusMessage;
        public Action<HObject> OnShowImage;
        public Action<BaseParam> OnUpdateParameters;
        public Action<InspectionStepResult> OnUpdateStepSpecificData;
        public Action OnBackButtonClicked;

        private MstPicbox.PicboxControl _picboxEdit;
        // 对外暴露的 WinForms Picbox 控件，用于在外部访问或进行图像显示操作
        public MstPicbox.PicboxControl PicBox => _picboxEdit;
        public TabControl MainTabControl => TabControl;

        public DynamicConfigView()
        {
            InitializeComponent();
            InitializeWinFormsControl();
        }

        // 初始化并配置嵌入的 WinForms Picbox 控件
        private void InitializeWinFormsControl()
        {
            _picboxEdit = new MstPicbox.PicboxControl
            {
                AllowDrop = true,
                AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink,
                AutoValidate = System.Windows.Forms.AutoValidate.EnableAllowFocusChange,
                BackColor = System.Drawing.Color.Transparent,
                ComChildVisibal = true,
                InnerRangeFlag = true,
                MenuStrip = false,
                PicboxBackColor = System.Drawing.Color.MidnightBlue,
                Dock = System.Windows.Forms.DockStyle.Fill
            };
            WinFormsHost.Child = _picboxEdit;
        }

        // 将配置引擎绑定到当前控件的 DataContext，供 XAML 进行数据绑定
        public void SetConfigEngine(ConfigEngineViewModel engine)
        {
            this.DataContext = engine;
        }

        // 继续保留的事件接口，供外部订阅特定交互
        public event EventHandler<KeyValuePair<string, BaseParam>> ParameterChanged;
        public event EventHandler RequestMainImageUpdate;
        public event EventHandler RunToThisTaskClicked;
        public event EventHandler LoadAndRunToThisTaskClicked;
        public event EventHandler<KeyValuePair<string, BaseParam>> SaveParameter;
        public event EventHandler BackButtonClicked;
        public event EventHandler<string[]> LoadAndRunToThisTasksClicked;

        // 将状态消息委托给外部处理者，并确保在 UI 线程上执行
        public void SetStatusMessage(string message)
        {
            if (OnSetStatusMessage == null) return;
            if (Dispatcher?.CheckAccess() ?? false)
            {
                OnSetStatusMessage(message);
            }
            else
            {
                Dispatcher?.BeginInvoke(new Action(() => OnSetStatusMessage(message)));
            }
        }

        // 将图像显示请求委托给外部处理者（例如在主窗体中显示 Halcon 图像）
        public void ShowImage(HObject imaHObject)
        {
            if (OnShowImage == null) return;
            if (Dispatcher?.CheckAccess() ?? false)
            {
                OnShowImage(imaHObject);
            }
            else
            {
                Dispatcher?.BeginInvoke(new Action(() => OnShowImage(imaHObject)));
            }
        }

        // 将参数更新操作委托给外部处理者（例如保存或反映到其他组件）
        public void UpdateParameters(BaseParam parameters)
        {
            if (OnUpdateParameters == null) return;
            if (Dispatcher?.CheckAccess() ?? false)
            {
                OnUpdateParameters(parameters);
            }
            else
            {
                Dispatcher?.BeginInvoke(new Action(() => OnUpdateParameters(parameters)));
            }
        }

        // 将步骤特定的数据更新委托给外部处理者（通常用于更新界面或流程数据）
        public void UpdateStepSpecificData(InspectionStepResult data)
        {
            if (OnUpdateStepSpecificData == null) return;
            if (Dispatcher?.CheckAccess() ?? false)
            {
                OnUpdateStepSpecificData(data);
            }
            else
            {
                Dispatcher?.BeginInvoke(new Action(() => OnUpdateStepSpecificData(data)));
            }
        }

        public void BackButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (OnBackButtonClicked == null) return;
            if (Dispatcher?.CheckAccess() ?? false)
            {
                OnBackButtonClicked();
            }
            else
            {
                Dispatcher?.BeginInvoke(new Action(() => OnBackButtonClicked()));
            }
        }
    }
}
