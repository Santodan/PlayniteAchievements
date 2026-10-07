param([string]$CoreAssembly = 'E:\Programs\PlaynitePortableClean\Extensions\PS5Core_84d85c00-14b1-4859-805f-6bd9d95fdae9\PS5Core.dll')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$application = New-Object Windows.Application
[Reflection.Assembly]::LoadFrom('E:\Programs\Playnite\Playnite.SDK.dll') | Out-Null
$fork = [Reflection.Assembly]::LoadFrom((Resolve-Path "$PSScriptRoot\..\..\source\bin\Release\PlayniteAchievements.dll").Path)
$core = [Reflection.Assembly]::LoadFrom($CoreAssembly)
$vm = [Activator]::CreateInstance($core.GetType('PS5Core.AchievementsViewModel'))
$itemType = $core.GetType('PS5Core.AchievementItem')
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
$items = $vm.GetType().GetField('_allAchievements',$flags).GetValue($vm)
$unlockItem = [Activator]::CreateInstance($itemType)
$unlockItem.ApiName = 'CAT_1'; $unlockItem.Name = 'Cat 1'
$lockedItem = [Activator]::CreateInstance($itemType)
$lockedItem.ApiName = 'CAT_2'; $lockedItem.Name = 'Cat 2'
$items.Add($unlockItem); $items.Add($lockedItem)
$unlockedButton = New-Object Windows.Controls.Button
$unlockedButton.DataContext = $vm
[Windows.Data.BindingOperations]::SetBinding($unlockedButton,[Windows.UIElement]::IsEnabledProperty,[Windows.Data.Binding]::new('CanSelectUnlocked')) | Out-Null
$application.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
if ($unlockedButton.IsEnabled) { throw 'Unlocked button should start disabled' }
$data = [Activator]::CreateInstance($fork.GetType('PlayniteAchievements.Models.Achievements.GameAchievementData'))
$state = [Activator]::CreateInstance($fork.GetType('PlayniteAchievements.Models.Achievements.AchievementDetail'))
$state.ApiName = 'CAT_1'; $state.Unlocked = $true; $state.UnlockTimeUtc = [datetime]'2026-10-08T12:00:00Z'
$data.Achievements.Add($state)
$method = $fork.GetType('PlayniteAchievements.Services.ThemeMigration.Ps5AchievementStateSynchronizer').GetMethod('Synchronize',[Reflection.BindingFlags]'NonPublic,Static')
$arguments = [object[]]@($vm.PSObject.BaseObject,$data.PSObject.BaseObject,$null)
if (!$method.Invoke($null,$arguments)) { throw 'Synchronization made no changes' }
$application.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
if (!$unlockedButton.IsEnabled) { throw 'Unlocked filter button remained disabled after synchronization' }
if (!$unlockItem.IsUnlocked -or $unlockItem.UnlockDate -ne $state.UnlockTimeUtc) { throw 'Unlock state or date not copied' }
if ($lockedItem.IsUnlocked) { throw 'Unmatched item changed' }
if ($method.Invoke($null,$arguments)) { throw 'Synchronization not idempotent' }
$filterType = $vm.GetType().GetProperty('AchievementFilter').PropertyType
$vm.AchievementFilter = [Enum]::Parse($filterType,'Unlocked')
$vm.GetType().GetMethod('ApplyAchievementFilterSort',$flags).Invoke($vm,@())
if ($vm.Achievements.Count -ne 1 -or $vm.Achievements[0].ApiName -ne 'CAT_1') { throw 'Unlocked filter not refreshed' }
$vm.AchievementFilter = [Enum]::Parse($filterType,'Locked')
$vm.GetType().GetMethod('ApplyAchievementFilterSort',$flags).Invoke($vm,@())
if ($vm.Achievements.Count -ne 1 -or $vm.Achievements[0].ApiName -ne 'CAT_2') { throw 'Locked filter not refreshed' }
$state.Unlocked = $false
if (!$method.Invoke($null,$arguments) -or $unlockItem.IsUnlocked -or $null -ne $unlockItem.UnlockDate -or $vm.Achievements.Count -ne 2) { throw 'Changed cached state did not update current locked filter' }
$application.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
if ($unlockedButton.IsEnabled) { throw 'Unlocked filter button remained enabled after cached state changed' }
'Passed against actual PS5Core: API-name matching, unlock dates, unmatched isolation, idempotence, unlocked/locked filters, and refreshed cached states.'
$state.Unlocked = $true
$method.Invoke($null,$arguments) | Out-Null
'Passed: live Unlocked filter button binding follows synchronized state changes.'
