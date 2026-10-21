dotnet restore && dotnet build --configuration Release
dotnet publish Jellyfin.Plugin.TorrentDownloader/Jellyfin.Plugin.TorrentDownloader.csproj --configuration Release --output ./bin/Release/net10.0/publish
version=$1
zip -rj jellyfin-torrent-$version.zip bin
md5output=`md5sum jellyfin-torrent-$version.zip`
echo $md5output | cut -f 1 -d " "
