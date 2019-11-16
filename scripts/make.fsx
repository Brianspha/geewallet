#!/usr/bin/env fsharpi

open System
open System.IO
open System.Linq
open System.Diagnostics

#r "System.Configuration"
#load "InfraLib/Misc.fs"
#load "InfraLib/Process.fs"
#load "InfraLib/Git.fs"
open FSX.Infrastructure
open Process

let UNIX_NAME = "geewallet"
let DEFAULT_SOLUTION_FILE = "gwallet.backend.sln"
let LINUX_SOLUTION_FILE = "gwallet.linux.sln"
let NETSTANDARD_SOLUTION_FILE = "gwallet.netstandard.sln"

let buildConfigFileName = "build.config"

type BinaryConfig =
    | Debug
    | Release
    override self.ToString() =
        sprintf "%A" self

let rec private GatherTarget (args: string list, targetSet: Option<string>): Option<string> =
    match args with
    | [] -> targetSet
    | head::tail ->
        if (targetSet.IsSome) then
            failwith "only one target can be passed to make"
        GatherTarget (tail, Some (head))

let scriptsDir = __SOURCE_DIRECTORY__ |> DirectoryInfo
let rootDir = Path.Combine(scriptsDir.FullName, "..") |> DirectoryInfo

let buildConfigContents =
    let buildConfig = FileInfo (Path.Combine (scriptsDir.FullName, buildConfigFileName))
    if not (buildConfig.Exists) then
        let configureLaunch =
            match Misc.GuessPlatform() with
            | Misc.Platform.Windows -> ".\\configure.bat"
            | _ -> "./configure.sh"
        Console.Error.WriteLine (sprintf "ERROR: configure hasn't been run yet, run %s first"
                                         configureLaunch)
        Environment.Exit 1

    let skipBlankLines line = not <| String.IsNullOrWhiteSpace line
    let splitLineIntoKeyValueTuple (line:string) =
        let pair = line.Split([|'='|], StringSplitOptions.RemoveEmptyEntries)
        if pair.Length <> 2 then
            failwithf "All lines in %s must conform to format:\n\tkey=value"
                      buildConfigFileName
        pair.[0], pair.[1]

    let buildConfigContents =
        File.ReadAllLines buildConfig.FullName
        |> Array.filter skipBlankLines
        |> Array.map splitLineIntoKeyValueTuple
        |> Map.ofArray
    buildConfigContents

let GetOrExplain key map =
    match map |> Map.tryFind key with
    | Some k -> k
    | None   -> failwithf "No entry exists in %s with a key '%s'."
                          buildConfigFileName key

let prefix = buildConfigContents |> GetOrExplain "Prefix"
let libPrefixDir = DirectoryInfo (Path.Combine (prefix, "lib", UNIX_NAME))
let binPrefixDir = DirectoryInfo (Path.Combine (prefix, "bin"))

let wrapperScript = """#!/usr/bin/env bash
set -eo pipefail

if [[ $SNAP ]]; then
    PKG_DIR=$SNAP/usr
    export MONO_PATH=$PKG_DIR/lib/mono/4.5:$PKG_DIR/lib/cli/gtk-sharp-2.0:$PKG_DIR/lib/cli/glib-sharp-2.0:$PKG_DIR/lib/cli/atk-sharp-2.0:$PKG_DIR/lib/cli/gdk-sharp-2.0:$PKG_DIR/lib/cli/pango-sharp-2.0:$MONO_PATH
    export MONO_CONFIG=$SNAP/etc/mono/config
    export MONO_CFG_DIR=$SNAP/etc
    export MONO_REGISTRY_PATH=~/.mono/registry
    export MONO_GAC_PREFIX=$PKG_DIR/lib/mono/gac/
fi

DIR_OF_THIS_SCRIPT=$(dirname "$(realpath "$0")")
FRONTEND_PATH="$DIR_OF_THIS_SCRIPT/../lib/$UNIX_NAME/$GWALLET_PROJECT.exe"
exec mono "$FRONTEND_PATH" "$@"
"""

let nugetExe = Path.Combine(rootDir.FullName, ".nuget", "nuget.exe") |> FileInfo
let nugetPackagesSubDirName = "packages"

type IProject =
    abstract ProjectName: string with get

let GetProjectName (project: IProject): string =
    project.ProjectName

let GetProjectPath (project: IProject): string =
    Path.Combine (rootDir.FullName, "src", project.ProjectName)

type BackendProject =
    | Backend
    | BackendTests
    interface IProject with
        member self.ProjectName: string =
            match self with
            | Backend -> "GWallet.Backend"
            | BackendTests -> "GWallet.Backend.Tests"

type Frontend =
    | Console
    | Gtk
    interface IProject with
        member self.ProjectName =
            match self with
            | Console -> "GWallet.Frontend.Console"
            | Gtk -> "GWallet.Frontend.XF.Gtk"
    override self.ToString() =
        sprintf "%A" self

let PrintNugetVersion () =
    if not (nugetExe.Exists) then
        false
    else
        let nugetProc = Process.Execute ({ Command = "mono"; Arguments = nugetExe.FullName }, Echo.Off)
        Console.WriteLine nugetProc.Output.StdOut
        if nugetProc.ExitCode = 0 then
            true
        else
            Console.Error.WriteLine nugetProc.Output.StdErr
            Console.WriteLine()
            failwith "nuget process' output contained errors ^"


let oldVersionOfMono =
    let monoVersion = Map.tryFind "MonoPkgConfigVersion" buildConfigContents
    match monoVersion with
    | None ->
        false
    | Some version ->
        let versionOfMonoWhereRunningExesDirectlyIsSupported = Version("5.16")
        let versionOfMonoWhereArrayEmptyIsPresent = Version("5.8.1.0")
        let maxVersion =
            [versionOfMonoWhereRunningExesDirectlyIsSupported;
             versionOfMonoWhereArrayEmptyIsPresent].Max()
        let currentMonoVersion = Version(version)
        1 = maxVersion.CompareTo currentMonoVersion

let BuildSolution buildTool solutionFileName binaryConfig extraOptions =
    let configOption = sprintf "/p:Configuration=%s" (binaryConfig.ToString())
    let configOptions =
        match buildConfigContents |> Map.tryFind "DefineConstants" with
        | Some constants -> sprintf "%s;DefineConstants=%s" configOption constants
        | None   -> configOption
    let buildArgs = sprintf "%s %s %s"
                            solutionFileName
                            configOptions
                            extraOptions
    let buildProcess = Process.Execute ({ Command = buildTool; Arguments = buildArgs }, Echo.All)
    if (buildProcess.ExitCode <> 0) then
        Console.Error.WriteLine (sprintf "%s build failed" buildTool)
        PrintNugetVersion() |> ignore
        Environment.Exit 1

let NugetRestoreString (path: string) =
    printfn "Restoring %s" path
    let packagesPath: string = Path.Combine (rootDir.FullName, nugetPackagesSubDirName)
    let nugetWorkaroundArgs =
        sprintf " restore %s -PackagesDirectory %s" path packagesPath
    let nugetCmd =
        match Misc.GuessPlatform() with
        | Misc.Platform.Windows ->
            { Command = nugetExe.FullName; Arguments = nugetWorkaroundArgs }
        | _ -> { Command = "mono"; Arguments = nugetExe.FullName + nugetWorkaroundArgs }
    Process.SafeExecute(nugetCmd, Echo.All) |> ignore
    printfn "Restored %s" path

let NugetRestore (project: IProject) =
    let projectName = project.ProjectName
    let path: string = GetProjectPath project
    NugetRestoreString path

let JustBuild binaryConfig: Option<Frontend*FileInfo> =
    printfn "Building in %s mode..." (binaryConfig.ToString().ToUpper())
    let buildTool = Map.tryFind "BuildTool" buildConfigContents
    if buildTool.IsNone then
        failwith "A BuildTool should have been chosen by the configure script, please report this bug"

    let nugetDir = DirectoryInfo ".nuget"
    if not nugetDir.Exists then
        nugetDir.Create()

    let nugetVersionToDownload =
        if oldVersionOfMono then
            "4.5.1"
        else
            "5.4.0"
    let nugetUrl = sprintf "https://dist.nuget.org/win-x86-commandline/v%s/nuget.exe"
                           nugetVersionToDownload
    if not nugetExe.Exists then
        Process.SafeExecute({ Command = "curl"; Arguments = sprintf "-o .nuget/nuget.exe %s" nugetUrl },
                            Echo.All) |> ignore
        nugetExe.Refresh()

    List.map NugetRestore [BackendProject.Backend; BackendProject.BackendTests] |> ignore

    BuildSolution buildTool.Value DEFAULT_SOLUTION_FILE binaryConfig String.Empty

    let restoreAndBuildSol sol =
        BuildSolution buildTool.Value sol binaryConfig "/t:Restore"
        // TODO: report as a bug the fact that /t:Restore;Build doesn't work while /t:Restore and later /t:Build does
        BuildSolution buildTool.Value sol binaryConfig "/t:Build"


    let frontend =
        // older mono versions (which only have xbuild, not msbuild) can't compile .NET Standard assemblies
        if oldVersionOfMono then
            None
        else
            NugetRestoreString NETSTANDARD_SOLUTION_FILE
            BuildSolution buildTool.Value NETSTANDARD_SOLUTION_FILE binaryConfig "/t:Build"

            if Misc.GuessPlatform () <> Misc.Platform.Linux then
                Some Frontend.Console
            else
                let pkgConfigForGtkProc = Process.Execute({ Command = "pkg-config"; Arguments = "gtk-sharp-2.0" }, Echo.All)
                let isGtkPresent =
                    (0 = pkgConfigForGtkProc.ExitCode)

                if isGtkPresent then
                    Some Frontend.Gtk
                else
                    Some Frontend.Console

    match frontend with
    | None -> None
    | Some frontend ->
        if frontend = Frontend.Gtk then
            // somehow, msbuild doesn't restore the dependencies of the GTK frontend (Xamarin.Forms in particular)
            // when targetting the LINUX_SOLUTION_FILE below, so we need this workaround. TODO: report this bug
            NugetRestore Frontend.Gtk

            restoreAndBuildSol LINUX_SOLUTION_FILE

        let scriptName = sprintf "%s-%s" UNIX_NAME (frontend.ToString().ToLower())
        let launcherScriptFile = FileInfo (Path.Combine (__SOURCE_DIRECTORY__, "bin", scriptName))
        Directory.CreateDirectory(launcherScriptFile.Directory.FullName) |> ignore
        let wrapperScriptWithPaths =
            wrapperScript.Replace("$TARGET_DIR", UNIX_NAME)
                         .Replace("$GWALLET_PROJECT", GetProjectName frontend)
        File.WriteAllText (launcherScriptFile.FullName, wrapperScriptWithPaths)
        Some(frontend, launcherScriptFile)

let MakeCheckCommand (commandName: string) =
    if not (Process.CommandWorksInShell commandName) then
        Console.Error.WriteLine (sprintf "%s not found, please install it first" commandName)
        Environment.Exit 1

let GetPathToFrontend (frontend: Frontend) (binaryConfig: BinaryConfig): DirectoryInfo*FileInfo =
    let frontendProjName = GetProjectName frontend
    let dir = Path.Combine (rootDir.FullName, "src", frontendProjName, "bin", binaryConfig.ToString())
                  |> DirectoryInfo
    let mainExecFile = dir.GetFiles("*.exe", SearchOption.TopDirectoryOnly).Single()
    dir,mainExecFile

let MakeAll() =
    let buildConfig = BinaryConfig.Debug
    match JustBuild buildConfig with
    | None -> None
    | Some (frontend, _) ->
        Some (frontend, buildConfig)

let RunFrontend (frontend: Frontend) (buildConfig: BinaryConfig) (maybeArgs: Option<string>) =
    let frontendDir,frontendExecutable = GetPathToFrontend frontend buildConfig
    let pathToFrontend = frontendExecutable.FullName

    let fileName, finalArgs =
        if oldVersionOfMono then
            match maybeArgs with
            | None | Some "" -> "mono",pathToFrontend
            | Some args -> "mono",pathToFrontend + " " + args
        else
            match maybeArgs with
            | None | Some "" -> pathToFrontend,String.Empty
            | Some args -> pathToFrontend,args

    let startInfo = ProcessStartInfo(FileName = fileName, Arguments = finalArgs, UseShellExecute = false)
    startInfo.EnvironmentVariables.["MONO_ENV_OPTIONS"] <- "--debug"

    let proc = Process.Start startInfo
    proc.WaitForExit()
    proc

let maybeTarget = GatherTarget (Misc.FsxArguments(), None)
match maybeTarget with
| None ->
    MakeAll() |> ignore

| Some("release") ->
    JustBuild BinaryConfig.Release
        |> ignore

| Some "nuget" ->
    Console.WriteLine "This target is for debugging purposes."

    if not (PrintNugetVersion()) then
        Console.Error.WriteLine "Nuget executable has not been downloaded yet, try `make` alone first"
        Environment.Exit 1

| Some("zip") ->
    let zipCommand = "zip"
    MakeCheckCommand zipCommand

    let version = Misc.GetCurrentVersion(rootDir).ToString()

    let release = BinaryConfig.Release
    let frontend,script =
        match JustBuild release with
        | None -> failwith "This system can't build a frontend, so can't zip a release"
        | Some (x, y) -> x, y

    let binDir = "bin"
    Directory.CreateDirectory(binDir) |> ignore

    let zipNameWithoutExtension = sprintf "%s-v%s" script.Name version
    let zipName = sprintf "%s.zip" zipNameWithoutExtension
    let pathToZip = Path.Combine(binDir, zipName)
    if (File.Exists (pathToZip)) then
        File.Delete (pathToZip)

    let pathToFolderToBeZipped = Path.Combine(binDir, zipNameWithoutExtension)
    if (Directory.Exists (pathToFolderToBeZipped)) then
        Directory.Delete (pathToFolderToBeZipped, true)

    let pathToFrontend,_ = GetPathToFrontend frontend release
    let zipRun = Process.Execute({ Command = "cp"
                                   Arguments = sprintf "-rfvp %s %s" pathToFrontend.FullName pathToFolderToBeZipped },
                                 Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "Precopy for ZIP compression failed"
        Environment.Exit 1

    let previousCurrentDir = Directory.GetCurrentDirectory()
    Directory.SetCurrentDirectory binDir
    let zipLaunch = { Command = zipCommand
                      Arguments = sprintf "%s -r %s %s"
                                      zipCommand zipName zipNameWithoutExtension }
    let zipRun = Process.Execute(zipLaunch, Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "ZIP compression failed"
        Environment.Exit 1
    Directory.SetCurrentDirectory previousCurrentDir

| Some("check") ->
    Console.WriteLine "Running tests..."
    Console.WriteLine ()

    let testAssemblyName = "GWallet.Backend.Tests"
    let testAssembly = Path.Combine(rootDir.FullName, "src", testAssemblyName, "bin",
                                    testAssemblyName + ".dll") |> FileInfo
    if not testAssembly.Exists then
        failwithf "File not found: %s" testAssembly.FullName

    let runnerCommand =
        match Misc.GuessPlatform() with
        | Misc.Platform.Linux ->
            let nunitCommand = "nunit-console"
            MakeCheckCommand nunitCommand

            { Command = nunitCommand; Arguments = testAssembly.FullName }
        | _ ->
            let nunitVersion = "2.7.1"
            if not nugetExe.Exists then
                MakeAll () |> ignore

            let nugetInstallCommand =
                {
                    Command = nugetExe.FullName
                    Arguments = sprintf "install NUnit.Runners -Version %s -OutputDirectory %s"
                                        nunitVersion nugetPackagesSubDirName
                }
            Process.SafeExecute(nugetInstallCommand, Echo.All)
                |> ignore

            {
                Command = Path.Combine(nugetPackagesSubDirName,
                                       sprintf "NUnit.Runners.%s" nunitVersion,
                                       "tools",
                                       "nunit-console.exe")
                Arguments = testAssembly.FullName
            }

    let nunitRun = Process.Execute(runnerCommand,
                                   Echo.All)
    if (nunitRun.ExitCode <> 0) then
        Console.Error.WriteLine "Tests failed"
        Environment.Exit 1

| Some("install") ->
    let buildConfig = BinaryConfig.Release
    let frontend,launcherScriptFile =
        match JustBuild buildConfig with
        | None -> failwith "This system can't build a frontend, so can't install"
        | Some (x, y) -> x, y

    let mainBinariesDir binaryConfig = DirectoryInfo (Path.Combine(rootDir.FullName,
                                                                   "src",
                                                                   GetProjectName frontend,
                                                                   "bin",
                                                                   binaryConfig.ToString()))


    let destDirUpperCase = Environment.GetEnvironmentVariable "DESTDIR"
    let destDirLowerCase = Environment.GetEnvironmentVariable "DestDir"
    let destDir =
        if not (String.IsNullOrEmpty destDirUpperCase) then
            destDirUpperCase |> DirectoryInfo
        elif not (String.IsNullOrEmpty destDirLowerCase) then
            destDirLowerCase |> DirectoryInfo
        else
            prefix |> DirectoryInfo

    let libDestDir = Path.Combine(destDir.FullName, "lib", UNIX_NAME) |> DirectoryInfo
    let binDestDir = Path.Combine(destDir.FullName, "bin") |> DirectoryInfo

    Console.WriteLine "Installing..."
    Console.WriteLine ()
    Misc.CopyDirectoryRecursively (mainBinariesDir buildConfig, libDestDir, [])

    let finalLauncherScriptInDestDir = Path.Combine(binDestDir.FullName, launcherScriptFile.Name) |> FileInfo
    if not (Directory.Exists(finalLauncherScriptInDestDir.Directory.FullName)) then
        Directory.CreateDirectory(finalLauncherScriptInDestDir.Directory.FullName) |> ignore
    File.Copy(launcherScriptFile.FullName, finalLauncherScriptInDestDir.FullName, true)
    if Process.Execute({ Command = "chmod"; Arguments = sprintf "ugo+x %s" finalLauncherScriptInDestDir.FullName },
                        Echo.Off).ExitCode <> 0 then
        failwith "Unexpected chmod failure, please report this bug"

| Some("run") ->
    let frontend, buildConfig =
        match MakeAll () with
        | None -> failwith "This system can't build a frontend, so can't run the 'run' target"
        | Some (x, y) -> x, y
    RunFrontend frontend buildConfig None
        |> ignore

| Some "update-servers" ->
    let buildConfig =
        match MakeAll () with
        | None -> failwith "This system can't build a frontend, so can't run 'update-servers' target"
        | Some (_, x) -> x
    Directory.SetCurrentDirectory (GetProjectPath BackendProject.Backend)
    let proc1 = RunFrontend Frontend.Console buildConfig (Some "--update-servers-file")
    if proc1.ExitCode <> 0 then
        Environment.Exit proc1.ExitCode
    else
        let proc2 = RunFrontend Frontend.Console buildConfig (Some "--update-servers-stats")
        Environment.Exit proc2.ExitCode

| Some(someOtherTarget) ->
    Console.Error.WriteLine("Unrecognized target: " + someOtherTarget)
    Environment.Exit 2
