using System;
using System.Windows;

namespace PathwinderMgt.Views
{
    /// <summary>
    /// 延迟展示附加属性：首次启动后满 N 天才显示挂载的元素，否则隐藏（Collapsed）。
    /// 用法：&lt;WrapPanel local:DelayedReveal.MinDays="3"&gt;。
    /// 仅支持控件（FrameworkElement）。文档元素（Hyperlink 等 FrameworkContentElement）
    /// 没有 Visibility 概念，属性挂上不报错但不生效——请挂到其外层的 TextBlock 上。
    /// 首次启动时间取自配置 Settings.FirstLaunchTime（启动协调器在首次运行时写入，
    /// 该时间不随配置重置以外的任何操作改变；用户改系统时间或删除配置可绕过，营销展示场景无需防护）。
    /// </summary>
    public static class DelayedReveal
    {
        /// <summary>挂载元素需要等待的天数：首次启动后满 N 天（不含当天）才显示。</summary>
        public static readonly DependencyProperty MinDaysProperty =
            DependencyProperty.RegisterAttached(
                "MinDays", typeof(double), typeof(DelayedReveal),
                new PropertyMetadata(double.NaN, OnMinDaysChanged));

        public static void SetMinDays(DependencyObject d, double value) => d.SetValue(MinDaysProperty, value);
        public static double GetMinDays(DependencyObject d) => (double)d.GetValue(MinDaysProperty);

        private static void OnMinDaysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // XAML 解析时即触发（此时 App.Bootstrap 已在 OnStartup 中就绪）
            if (d is FrameworkElement fe && !double.IsNaN((double)e.NewValue))
                Apply(fe);
        }

        private static void Apply(FrameworkElement fe)
        {
            double days = GetMinDays(fe);
            DateTime? first = App.Bootstrap?.Config?.Settings?.FirstLaunchTime;
            fe.Visibility = first.HasValue && (DateTime.Now - first.Value).TotalDays > days
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
