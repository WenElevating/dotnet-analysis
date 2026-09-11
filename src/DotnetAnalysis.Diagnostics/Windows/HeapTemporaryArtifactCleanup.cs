namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 为调用专属堆工件工作目录提供尽力清理；临时文件锁或权限变化不得覆盖捕获、索引或查询的主结果。
/// </summary>
internal static class HeapTemporaryArtifactCleanup
{
    /// <summary>
    /// 遗留调用目录至少静置一天后才允许回收，避免触碰仍在执行的长时间索引或压力任务。
    /// </summary>
    internal static readonly TimeSpan AbandonedDirectoryMinimumAge = TimeSpan.FromDays(1);

    /// <summary>
    /// 创建指定长度的调用专属临时文件，再由工厂打开其资源对象；打开失败时尽力删除文件并原样重抛主异常。
    /// </summary>
    /// <typeparam name="T">成功打开后由调用方管理生命周期的资源类型。</typeparam>
    /// <param name="path">必须尚不存在的精确临时文件路径。</param>
    /// <param name="length">创建后设置的非负文件长度。</param>
    /// <param name="open">文件句柄关闭后打开映射或其他资源的工厂。</param>
    /// <returns>成功打开的资源对象。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="open"/> 为空时引发。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 为负数时引发。</exception>
    internal static T CreateFileAndOpen<T>(string path, long length, Func<T> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(open);
        var created = false;
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                created = true;
                stream.SetLength(length);
            }

            return open();
        }
        catch
        {
            if (created)
            {
                TryDeleteFile(path);
            }

            throw;
        }
    }

    /// <summary>
    /// 尝试递归删除调用方拥有的临时目录；目录不存在视为已完成，短暂 IO 或权限失败留给后续回收。
    /// </summary>
    /// <param name="directory">已由调用方确认归属的精确临时目录。</param>
    internal static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时清理不能覆盖捕获、索引或查询的主结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 权限变化时保留调用专属目录，后续恢复流程仍可识别其临时命名。
        }
    }

    /// <summary>
    /// 尝试删除单个调用专属临时文件；锁占用或权限变化保留文件供外层目录的后续恢复扫描处理。
    /// </summary>
    /// <param name="path">调用方拥有的精确临时文件路径。</param>
    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // finally 清理不能覆盖构建失败或取消。
        }
        catch (UnauthorizedAccessException)
        {
            // 权限变化时保留文件，由后续目录恢复扫描再次处理。
        }
    }

    /// <summary>
    /// 尽力回收超过年龄门槛的调用专属目录；近期目录视为可能仍在使用，锁定旧目录留待下次扫描。
    /// </summary>
    /// <param name="parentDirectory">只在其直接子目录中扫描的受管父目录。</param>
    /// <param name="searchPattern">限定单一工作区命名族的目录模式。</param>
    /// <param name="minimumAge">允许回收前必须达到的最小静置时间。</param>
    internal static void TryDeleteAbandonedDirectories(
        string parentDirectory,
        string searchPattern,
        TimeSpan minimumAge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAge, TimeSpan.Zero);
        string[] directories;
        try
        {
            if (!Directory.Exists(parentDirectory))
            {
                return;
            }

            directories = Directory.GetDirectories(parentDirectory, searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - minimumAge;
        foreach (var directory in directories)
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) <= cutoff)
                {
                    TryDeleteDirectory(directory);
                }
            }
            catch (IOException)
            {
                // 单个遗留项失败不阻碍同族其他目录回收。
            }
            catch (UnauthorizedAccessException)
            {
                // 权限变化的目录保留到下次安全扫描。
            }
        }
    }
}
