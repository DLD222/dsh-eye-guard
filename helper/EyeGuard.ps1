$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$sourcePath = Join-Path $PSScriptRoot 'EyeGuard.cs'
$source = [System.IO.File]::ReadAllText($sourcePath)
$references = @(
    'System.Windows.Forms',
    'System.Drawing',
    'WindowsBase',
    'PresentationCore',
    'PresentationFramework',
    'System.Xaml',
    'UIAutomationTypes',
    'UIAutomationProvider',
    'System.Web.Extensions'
)
Add-Type -TypeDefinition $source -Language CSharp -ReferencedAssemblies $references -ErrorAction Stop
[DshEyeGuard.Program]::Run()
