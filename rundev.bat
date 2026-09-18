git submodule update --init
dotnet build && dotnet test
dotnet run --project src/DarkHaven.Cli -- probe --hub
dotnet run --project src/DarkHaven.App