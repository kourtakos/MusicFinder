<#
.SYNOPSIS
  Writes src\MusicFinder.App\Resources\Strings.el.resx from one or more JSON files {"English text": "Greek text"}.
  Existing entries are kept; entries from the JSON files are added or replace them.
#>
param([Parameter(Mandatory)][string[]]$Json)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue
$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\MusicFinder.App\Resources\Strings.el.resx'
$map = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
if (Test-Path $target) {
    $xml = [xml](Get-Content $target -Raw -Encoding UTF8)
    foreach ($d in $xml.root.data) { $map[$d.name] = $d.value }
}
foreach ($file in $Json) {
    $dict = $ser.DeserializeObject((Get-Content $file -Raw -Encoding UTF8))
    foreach ($k in $dict.Keys) {
        $key = ([string]$k).Trim(); $val = [string]$dict[$k]
        if ($map.ContainsKey($key) -and $map[$key] -ne $val) { Write-Warning "Conflict for '$key': '$($map[$key])' vs '$val' (keeping the new one)" }
        $map[$key] = $val
    }
}
$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true; $settings.Encoding = New-Object System.Text.UTF8Encoding($true)
$w = [System.Xml.XmlWriter]::Create($target, $settings)
$w.WriteStartDocument(); $w.WriteComment(' Greek UI texts. The key is the English text used in the code and XAML. Edit with care: keep {0} placeholders. ')
$w.WriteStartElement('root')
foreach ($h in @(@('resmimetype','text/microsoft-resx'),@('version','2.0'),@('reader','System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'),@('writer','System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'))) {
    $w.WriteStartElement('resheader'); $w.WriteAttributeString('name',$h[0]); $w.WriteElementString('value',$h[1]); $w.WriteEndElement()
}
foreach ($k in $map.Keys) {
    $w.WriteStartElement('data'); $w.WriteAttributeString('name',$k); $w.WriteAttributeString('xml','space','http://www.w3.org/XML/1998/namespace','preserve')
    $w.WriteElementString('value',$map[$k]); $w.WriteEndElement()
}
$w.WriteEndElement(); $w.WriteEndDocument(); $w.Close()
Write-Host "Strings.el.resx: $($map.Count) entries"
