namespace AxTools.Core.Models;

public enum ManagedToolActionSection
{
    Development,
    Release,
    Publish
}

public enum ManagedToolAction
{
    CheckEnvironment,
    DownloadSource,
    RunDevelopment,
    RunExistingDevelopment,
    Build,
    BuildAndRun,
    ForceBuildAndRun,
    Test,
    SourceHealthCheck,
    RunRelease,
    DownloadRelease,
    GetDownloadLink,
    BuildFrontend,
    TestFrontend,
    TestRust,
    BuildAndroidDebug,
    BuildAndroidAndInstall,
    BuildAndroidRelease,
    ListAndroidDevices,
    StopAndroidApp,
    PackageStable,
    PackageBeta,
    BuildLauncherPackage,
    SetVersion,
    ValidatePackage,
    PackageX64,
    CheckUnrealSyncEnvironment,
    InspectArtifacts,
    OpenDevelopmentOutput,
    OpenReleaseDirectory,
    OpenWorkspace,
    ValidateAndStagePackage,
    ReplaceRelease,
    UploadDryRun,
    Upload,
    PublishDryRun,
    Publish,
    BuildGameChunks,
    UploadGameChunks,
    PublishGamePackage
    ,EditLauncherReleaseNotes
    ,EditGameReleaseNotes
}
