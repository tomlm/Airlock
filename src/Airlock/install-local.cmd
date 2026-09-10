dotnet tool uninstall -g Airlock.Cli
dotnet pack -c Release
dotnet tool install -g Airlock.Cli --source nupkg