using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DotnetAnalysis.Desktop.Infrastructure;

/// <summary>
/// 提供属性变更通知和通用字段更新辅助方法的基类。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>
    /// 属性值发生变化时通知绑定目标。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 仅在值确实变化时更新字段并发出属性通知。
    /// </summary>
    /// <typeparam name="T">属性值类型。</typeparam>
    /// <param name="field">要更新的字段。</param>
    /// <param name="value">候选新值。</param>
    /// <param name="propertyName">属性名称，默认由调用方成员名推断。</param>
    /// <returns>值发生变化并已通知时返回 <see langword="true"/>。</returns>
    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 发出属性变化通知。
    /// </summary>
    /// <param name="propertyName">属性名称，默认由调用方成员名推断。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
