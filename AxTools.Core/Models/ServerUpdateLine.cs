namespace AxTools.Core.Models;

/// <summary>发布区「服务器更新」里的一行键值，供 ItemsControl 直接绑定。</summary>
public sealed record ServerUpdateLine(string Label, string Value);
