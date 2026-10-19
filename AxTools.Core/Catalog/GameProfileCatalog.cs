using AxTools.Core.Models;

namespace AxTools.Core.Catalog;

/// <summary>
/// 一个游戏的碎片/发布档案。所有跟游戏名字相关的东西集中在这里，
/// 碎片算法本身不再判断“零境还是幻杀”。
/// </summary>
public sealed record GameProfile(
    string Key,
    string DisplayName,
    string ShortName,
    string ProductSegment,
    string VersionMarkerFileName,
    string ObbSidecarFileName,
    string PcFragmentDirectoryName,
    string AndroidFragmentDirectoryName,
    ManagedToolKey GameToolKey,
    ManagedToolKey PcToolKey,
    ManagedToolKey AndroidToolKey)
{
    /// <summary>下载站上的产品段：PC 原样，安卓加 <c>-android</c>。</summary>
    public string ProductSegmentFor(bool android) =>
        android ? $"{ProductSegment}-android" : ProductSegment;
}

/// <summary>
/// 当前接入碎片的游戏档案。以后加火影/别的游戏，只在这里加一条，
/// 不要再去改 <c>CrossingVoid*</c> 服务里的文件名字面量。
/// </summary>
public static class GameProfileCatalog
{
    public static GameProfile CrossingVoid { get; } = new(
        Key: "crossingvoid",
        DisplayName: "零境交错",
        ShortName: "零境",
        ProductSegment: "crossingvoid",
        VersionMarkerFileName: "CrossingVoid.version.json",
        ObbSidecarFileName: "CrossingVoid.obb.json",
        PcFragmentDirectoryName: "零境交错PC碎片",
        AndroidFragmentDirectoryName: "零境交错安卓碎片",
        GameToolKey: ManagedToolKey.CrossingVoidGame,
        PcToolKey: ManagedToolKey.CrossingVoidPc,
        AndroidToolKey: ManagedToolKey.CrossingVoidAndroid);

    public static GameProfile Fantasy { get; } = new(
        Key: "fantasyproject",
        DisplayName: "幻杀",
        ShortName: "幻杀",
        ProductSegment: "fantasyproject",
        VersionMarkerFileName: "FantasyProject.version.json",
        ObbSidecarFileName: "FantasyProject.obb.json",
        PcFragmentDirectoryName: "幻杀PC碎片",
        AndroidFragmentDirectoryName: "幻杀安卓碎片",
        GameToolKey: ManagedToolKey.FantasyGame,
        PcToolKey: ManagedToolKey.FantasyProjectPc,
        AndroidToolKey: ManagedToolKey.FantasyAndroid);

    public static IReadOnlyList<GameProfile> All { get; } = [CrossingVoid, Fantasy];

    /// <summary>按 PC/安卓工具页的 Key 找档案；非碎片页返回 null。</summary>
    public static GameProfile? FindByToolKey(ManagedToolKey key) =>
        All.FirstOrDefault(profile => profile.PcToolKey == key || profile.AndroidToolKey == key);

    /// <summary>按“游戏包发布”工具的 Key 找档案。</summary>
    public static GameProfile? FindByGameToolKey(ManagedToolKey key) =>
        All.FirstOrDefault(profile => profile.GameToolKey == key);
}
