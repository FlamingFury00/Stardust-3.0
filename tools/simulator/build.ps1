$ErrorActionPreference = 'Stop'
$native = Join-Path $PSScriptRoot 'native'
$build = Join-Path $native 'build'
$configure = @('-S', $native, '-B', $build, '-DCMAKE_BUILD_TYPE=Release')
if ($env:ROCKETSIM_SOURCE_DIR) {
    $configure += "-DROCKETSIM_SOURCE_DIR=$env:ROCKETSIM_SOURCE_DIR"
}
& cmake @configure
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& cmake --build $build --config Release --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet build (Join-Path $PSScriptRoot 'Stardust.Simulator/Stardust.Simulator.csproj') --configuration Release
exit $LASTEXITCODE
