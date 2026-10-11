# Native Nexus regression checks

These checks invoke the compiled TorchMonitor plugin against the same native Space Engineers and Torch libraries used to build it. They cover translated sector bounds, segment mapping, invalid geometry, center-count overflow, empty populations, and zero-count telemetry when players leave a segment. The fixture creates no world or player objects and sends no telemetry or GPS messages.

Prerequisites: Windows, the .NET Framework 4.8 targeting pack, Visual Studio MSBuild, and .NET SDK 10.0.401. Use a private checkout with version-matched static libraries in `TorchBinaries`, `GameBinaries`, and `extern`. Keep game saves, journals, databases, and production configuration outside the checkout.

From the repository root, initialize the pinned TorchUtils submodule, apply the reviewed compatibility patch, and build with deployment disabled:

```powershell
git submodule update --init TorchUtils
if ($LASTEXITCODE -ne 0) { throw 'Submodule initialization failed' }
& .\TorchMonitor\prepare-tebex-build.ps1
if ($LASTEXITCODE -ne 0) { throw 'Compatibility preparation failed' }
$repo = (Get-Location).Path
MSBuild.exe .\TorchMonitor\TorchMonitor.csproj /t:Build /p:Configuration=Release /p:NoDeploy=true "/p:SolutionDir=$repo\"
if ($LASTEXITCODE -ne 0) { throw 'TorchMonitor build failed' }
Push-Location .\tools\NativeNexusRegression
try {
    dotnet build .\NativeNexusRegression.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Regression build failed' }
    & .\bin\Release\net48\NativeNexusRegression.exe $repo
    if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed' }
}
finally { Pop-Location }
```

The .NET SDK is pinned locally by this fixture's `global.json`. The executable resolves dependencies from the supplied checkout. A successful run prints the passed check count; it does not establish in-game command, GPS, profiler, or InfluxDB acceptance.
