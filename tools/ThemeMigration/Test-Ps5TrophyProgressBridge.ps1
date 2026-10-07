$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type 'public class Ps5Row { public int PlatinumCount {get;set;} public int GoldCount {get;set;} public int SilverCount {get;set;} public int BronzeCount {get;set;} public int Total {get;set;} public int ProgressPercent {get{return 0;}} public string ProgressText {get{return "0/100";}} }'
$app = New-Object Windows.Application
# Theme must parse successfully before the plugin assembly is loaded.
$xaml = @'
<ListBox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Uid="PlayAch.Ps5TrophyProgress"><!-- AchievementsViewModel.Games Plugin=PS5Core --><ListBox.ItemTemplate><DataTemplate><StackPanel><TextBlock Text="{Binding ProgressPercent}"/><TextBlock Text="{Binding ProgressText}"/><Rectangle Height="4" Width="{Binding ProgressPercent}"/></StackPanel></DataTemplate></ListBox.ItemTemplate></ListBox>
'@
$list = [Windows.Markup.XamlReader]::Parse($xaml)
[Reflection.Assembly]::LoadFrom('E:\Programs\Playnite\Playnite.SDK.dll') | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path "$PSScriptRoot\..\..\source\bin\Release\PlayniteAchievements.dll").Path)
$bridge = $assembly.GetType('PlayniteAchievements.Services.ThemeMigration.Ps5TrophyProgressBridge')
$bridge.GetMethod('Register', [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, @())
$row = New-Object Ps5Row -Property @{BronzeCount=4;Total=100}
$list.Items.Add($row.PSObject.BaseObject) | Out-Null
$list.Measure([Windows.Size]::new(600,400))
$list.Arrange([Windows.Rect]::new(0,0,600,400))
$list.UpdateLayout()
$controls = New-Object 'System.Collections.Generic.List[System.Windows.FrameworkElement]'
function Visit($node) {
    if ($node -is [Windows.Controls.TextBlock] -or $node -is [Windows.Shapes.Rectangle]) {
        $bindingProperty = if ($node -is [Windows.Controls.TextBlock]) { [Windows.Controls.TextBlock]::TextProperty } else { [Windows.FrameworkElement]::WidthProperty }
        $binding = [Windows.Data.BindingOperations]::GetBinding($node,$bindingProperty)
        if ($binding.Path.Path -in @('ProgressPercent','ProgressText')) {
            $controls.Add($node)
            $node.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.FrameworkElement]::LoadedEvent,$node))
        }
    }
    for($i=0;$i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($node);$i++) { Visit ([Windows.Media.VisualTreeHelper]::GetChild($node,$i)) }
}
Visit $list
$list.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
if ($controls.Count -ne 3) { throw "Expected three progress controls, got $($controls.Count)" }
if ($controls[0].Text -ne '4' -or $controls[1].Text -ne '4/100' -or $controls[2].Width -ne 4) { throw "Initial progress incorrect: $($controls[0].Text), $($controls[1].Text), $($controls[2].Width)" }
foreach($control in $controls) { $control.DataContext = New-Object Ps5Row -Property @{BronzeCount=19;Total=67} }
$list.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
if ($controls[0].Text -ne '28' -or $controls[1].Text -ne '19/67' -or $controls[2].Width -ne 28) { throw 'Refreshed progress incorrect' }
$migration = $assembly.GetType('PlayniteAchievements.Services.ThemeMigration.ThemeMigrationService').GetMethod('MigratePs5TrophyProgress',[Reflection.BindingFlags]'Static,NonPublic')
$migrationArgs = [object[]]@($xaml.Replace(' Uid="PlayAch.Ps5TrophyProgress"',''),0)
$migrated = $migration.Invoke($null,$migrationArgs)
if (!$migrated.Contains('Uid="PlayAch.Ps5TrophyProgress"') -or $migrationArgs[1] -ne 1) { throw 'Migration marker missing' }
$again = [object[]]@($xaml,0)
if ($migration.Invoke($null,$again) -ne $xaml -or $again[1] -ne 0) { throw 'Migration not idempotent' }
'Passed: theme loads before plugin; marked list displays 4/100 and 4%; refresh displays 19/67 and 28%; migration is idempotent.'

$coreAssembly = [Reflection.Assembly]::LoadFrom('E:\Programs\PlaynitePortableClean\Extensions\PS5Core_84d85c00-14b1-4859-805f-6bd9d95fdae9\PS5Core.dll')
$coreType = $coreAssembly.GetType('PS5Core.GameAchievementsItem')
$coreRow = [Activator]::CreateInstance($coreType)
$coreRow.Provider = ''; $coreRow.Total = 100; $coreRow.BronzeCount = 4
$coreList = New-Object Windows.Controls.ListBox
$coreList.Uid = 'PlayAch.Ps5TrophyProgress'
$coreList.Items.Add($coreRow) | Out-Null
$coreList.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.FrameworkElement]::LoadedEvent,$coreList))
if ($coreRow.Unlocked -ne 4 -or $coreRow.ProgressPercent -ne 4 -or $coreRow.ProgressText -ne '4/100') { throw 'Actual PS5Core Local row not corrected' }
$replacement = [Activator]::CreateInstance($coreType)
$replacement.Provider = 'Local'; $replacement.Total = 67; $replacement.BronzeCount = 19
$coreList.Items.Clear(); $coreList.Items.Add($replacement) | Out-Null
if ($replacement.Unlocked -ne 19 -or $replacement.ProgressPercent -ne 28) { throw 'Replacement PS5Core row not corrected' }
$replacement.Unlocked = 0
$connectionProperty = $bridge.GetField('ConnectionProperty',[Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
$connection = $coreList.GetValue($connectionProperty)
# A deterministic tick checks PS5Core's setters that omit PropertyChanged.
$connection.GetType().GetMethod('Correct',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($connection,@($replacement))
if ($replacement.Unlocked -ne 19) { throw 'Reset unlocked count not corrected' }
$other = [Activator]::CreateInstance($coreType)
$other.Provider = 'Steam'; $other.Total = 100; $other.BronzeCount = 4
$coreList.Items.Add($other) | Out-Null
if ($other.Unlocked -ne 4) { throw 'Explicit provider row not reconciled with trophy counts' }
$coreList.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.FrameworkElement]::UnloadedEvent,$coreList))
$replacement.Unlocked = 0
if ($replacement.Unlocked -ne 0) { throw 'Row listener retained after unload' }
'Passed against actual PS5Core 0.7.4: Local correction, row replacement, unlocked reset, non-Local isolation, unload cleanup.'

# Reproduce a window whose Loaded event was missed and whose copied template lost its marker.
$coreList.Uid = ''
$coreList.Name = 'PART_AchievementGamesList'
$bridge.GetField('_enableNamedList',[Reflection.BindingFlags]'NonPublic,Static').SetValue($null,$true)
$bridge.GetMethod('Discover',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,@($coreList.PSObject.BaseObject))
if ($replacement.Unlocked -ne 19) { throw 'Late discovery did not correct the live PS5Core row' }
$coreList.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.FrameworkElement]::UnloadedEvent,$coreList))
$bridge.GetMethod('Stop',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,@())
'Passed: late discovery attaches without relying on the original Loaded event or copied marker.'

Add-Type 'public class Ps5DetailContext { public Ps5DetailVm AchievementsViewModel {get;set;} public object OverlayTrophyGame {get;set;} } public class Ps5DetailVm { public object SelectedGame {get;set;} }'
$detailXaml = @'
<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding AchievementsViewModel.SelectedGame.ProgressPercent}"/><TextBlock Text="{Binding AchievementsViewModel.SelectedGame.Unlocked}"/><Rectangle Width="{Binding AchievementsViewModel.SelectedGame.ProgressPercent}"/><TextBlock Text="{Binding AchievementsViewModel.SelectedGame.Total}"/><TextBlock Text="{Binding OverlayTrophyGame.ProgressPercent}"/></StackPanel>
'@
$detail = [Windows.Markup.XamlReader]::Parse($detailXaml)
$selectedRow = [Activator]::CreateInstance($coreType)
$selectedRow.Provider = 'PC (Windows)'; $selectedRow.Total = 100; $selectedRow.BronzeCount = 11
$vm = New-Object Ps5DetailVm -Property @{SelectedGame=$selectedRow.PSObject.BaseObject}
$context = New-Object Ps5DetailContext -Property @{AchievementsViewModel=$vm; OverlayTrophyGame=$selectedRow.PSObject.BaseObject}
$detail.DataContext = $context
$bridge.GetField('_enableNamedList',[Reflection.BindingFlags]'NonPublic,Static').SetValue($null,$true)
$discover = $bridge.GetMethod('Discover',[Reflection.BindingFlags]'NonPublic,Static')
$discover.Invoke($null,@($detail.PSObject.BaseObject))
$detail.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
if ($detail.Children[0].Text -ne '11' -or $detail.Children[1].Text -ne '11' -or $detail.Children[2].Width -ne 11 -or $detail.Children[3].Text -ne '100' -or $detail.Children[4].Text -ne '11') { throw "Detail header incorrect: $($detail.Children[0].Text), $($detail.Children[1].Text), $($detail.Children[2].Width), $($detail.Children[4].Text)" }
# Navigation can construct a fresh selected-game object with its own stale total.
$vm = New-Object Ps5DetailVm -Property @{SelectedGame=$replacement.PSObject.BaseObject}
$detail.DataContext = New-Object Ps5DetailContext -Property @{AchievementsViewModel=$vm; OverlayTrophyGame=$replacement.PSObject.BaseObject}
$discover.Invoke($null,@($detail.PSObject.BaseObject))
$detail.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
if ($detail.Children[0].Text -ne '28' -or $detail.Children[1].Text -ne '19' -or $detail.Children[2].Width -ne 28 -or $detail.Children[3].Text -ne '67') { throw 'Detail header did not follow game navigation' }
$replacement.Provider = 'Steam'; $replacement.Unlocked = 7
$discover.Invoke($null,@($detail.PSObject.BaseObject))
$detail.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
if ($detail.Children[0].Text -ne '28' -or $detail.Children[1].Text -ne '19') { throw 'Detail header provider label caused inconsistent progress' }
$bridge.GetMethod('Stop',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,@())
'Passed: selected-game header with PC (Windows) provider (observed in live logs), 11/100 earned, 11% bar/overlay, navigation, and consistent explicit-provider progress.'
