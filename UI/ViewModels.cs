using HandyControl.Tools.Command;
using System;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace VisionTool.UI
{
	public class DynamicButtonModel
	{
		public string Text { get; set; }
		public ICommand Command { get; set; }
	}
	public class DynamicTabModel
	{
		public string Header { get; set; }
		public object ConfigObject { get; set; }
		public ObservableCollection<DynamicButtonModel> Buttons { get; set; } = new ObservableCollection<DynamicButtonModel>();
	}
	public class ConfigEngineViewModel
	{
		public ObservableCollection<DynamicTabModel> Tabs { get; set; } = new ObservableCollection<DynamicTabModel>();
		public ObservableCollection<DynamicButtonModel> GlobalButtons { get; set; } = new ObservableCollection<DynamicButtonModel>();

		/// <summary>
		/// 添加一个配置模块
		/// </summary>
		/// <typeparam name="T">配置类的类型 (如 EdgeConfig)</typeparam>
		/// <param name="header">Tab 标题</param>
		/// <param name="config">配置实例</param>
		/// <param name="buttons">按钮定义元组：(按钮文字, 点击回调)</param>
		public void AddModule<T>(string header, T config, params (string btnText, Action<T> onClick)[] buttons)
		{
			var tab = new DynamicTabModel
			{
				Header = header,
				ConfigObject = config
			};

			// 动态生成按钮并绑定 RelayCommand
			if (buttons != null)
			{
				foreach (var btn in buttons)
				{
					// 这里利用闭包，把 config 实例直接传回给回调
					// 这样外部调用时，直接能拿到强类型的 config 对象
					var command = new RelayCommand(_ => btn.onClick(config));

					tab.Buttons.Add(new DynamicButtonModel
					{
						Text = btn.btnText,
						Command = command
					});
				}
			}

			Tabs.Add(tab);
		}

		/// <summary>
		/// 添加一个具有指定显示文本的全局按钮，以及单击按钮时要执行的操作.
		/// </summary>
		/// <param name="text">要在全局按钮上显示的文本.</param>
		/// <param name="onClick">单击按钮时要执行的操作。如果为空，则单击按钮时不会执行任何操作.</param>
		public void AddGlobalButton(string text, Action onClick)
		{
			var command = new RelayCommand(_ => onClick?.Invoke());
			GlobalButtons.Add(new DynamicButtonModel
			{
				Text = text,
				Command = command
			});
		}
	}
}