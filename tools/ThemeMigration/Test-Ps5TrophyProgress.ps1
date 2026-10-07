$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::LoadFrom('E:\Programs\Playnite\Playnite.SDK.dll') | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path "$PSScriptRoot\..\..\source\bin\Release\PlayniteAchievements.dll").Path)
$method = $assembly.GetType('PlayniteAchievements.Services.ThemeMigration.ThemeMigrationService').GetMethod('MigratePs5TrophyProgress', [Reflection.BindingFlags]'NonPublic,Static')
$legacy = '<TextBlock Text="{playAchProgress:Ps5TrophyProgress Counter=True}"/><Rectangle Width="{playAchProgress:Ps5TrophyProgress Counter=False}"/>'
$broken = '<TextBlock><TextBlock.Text><MultiBinding Mode="OneWay" Converter="{StaticResource PlayAch.Ps5TrophyProgress.Counter}"><Binding Path="Total"/></MultiBinding></TextBlock.Text></TextBlock><Rectangle><Rectangle.Width><MultiBinding Mode="OneWay" Converter="{StaticResource PlayAch.Ps5TrophyProgress.Percent}"><Binding Path="Total"/></MultiBinding></Rectangle.Width></Rectangle>'
$inline = $broken.Replace(' Converter="{StaticResource PlayAch.Ps5TrophyProgress.Counter}">', '><MultiBinding.Converter><playAchProgress:Ps5TrophyProgressConverter Counter="True" /></MultiBinding.Converter>').Replace(' Converter="{StaticResource PlayAch.Ps5TrophyProgress.Percent}">', '><MultiBinding.Converter><playAchProgress:Ps5TrophyProgressConverter Counter="False" /></MultiBinding.Converter>')
foreach ($row in @($legacy, $broken, $inline)) {
    $inputText = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:playAchProgress="clr-namespace:PlayniteAchievements.Services.ThemeMigration;assembly=PlayniteAchievements"><ResourceDictionary.MergedDictionaries><ResourceDictionary Source="pack://application:,,,/PlayniteAchievements;component/Resources/Ps5TrophyProgress.xaml" /></ResourceDictionary.MergedDictionaries><ListBox>' + $row + '</ListBox></ResourceDictionary>'
    $arguments = [object[]]@($inputText, 0)
    $repaired = $method.Invoke($null, $arguments)
    [xml]$document = $repaired
    if ($repaired.Contains('Ps5TrophyProgress') -or $repaired.Contains('playAchProgress')) { throw 'Broken dependency remains' }
    if (!$repaired.Contains('{Binding ProgressText}') -or !$repaired.Contains('{Binding ProgressPercent}')) { throw 'Original bindings not restored' }
    $again = [object[]]@($repaired, 0)
    if ($method.Invoke($null, $again) -ne $repaired -or $again[1] -ne 0) { throw 'Repair not idempotent' }
}
'Recovery passed: three migration formats, original bindings, valid XML, idempotence.'
