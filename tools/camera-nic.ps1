<#
.SYNOPSIS
    Checks (default) or applies (-Apply) GigE Vision settings on the camera network adapter.

.DESCRIPTION
    Without -Apply nothing is changed and no administrator rights are needed. The adapter is the
    only Intel I210 in the PC unless -AdapterName names another one.

    -Apply needs an elevated PowerShell and sets, on that one adapter: jumbo frames, the largest
    receive buffer pool, a static IPv4 address without a gateway, and an inbound UDP firewall rule
    limited to this interface. It refuses an adapter with a default gateway, because that is the
    PC's internet connection. Use -WhatIf to preview.

    Undo: Remove-NetFirewallRule -DisplayName 'CamBridge GigE Vision'
          Set-NetIPInterface -InterfaceAlias <name> -Dhcp Enabled

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\camera-nic.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\camera-nic.ps1 -Apply
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$AdapterName,
    [switch]$Apply,
    [string]$HostIp = '192.168.10.1',
    [int]$PrefixLength = 24,
    [int]$JumboPacket = 9014
)

$ErrorActionPreference = 'Stop'
$FirewallRuleName = 'CamBridge GigE Vision'

function Write-Check([bool]$Ok, [string]$Text) {
    $tag = if ($Ok) { '[ OK ]' } else { '[WARN]' }
    Write-Output "$tag $Text"
}

function Get-AdvancedProperty([string]$Name, [string]$Keyword) {
    Get-NetAdapterAdvancedProperty -Name $Name -RegistryKeyword $Keyword -ErrorAction SilentlyContinue
}

function Get-RegistryNumber($Property) {
    if ($null -eq $Property) { return $null }
    return [int](@($Property.RegistryValue)[0])
}

function Wait-AdapterReady([string]$Name) {
    # Changing an advanced property restarts the adapter; its IP interface is gone for a few seconds.
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $adapter = Get-NetAdapter -Name $Name -ErrorAction SilentlyContinue
        $ipInterface = Get-NetIPInterface -InterfaceAlias $Name -AddressFamily IPv4 -ErrorAction SilentlyContinue
        if ($adapter -and $ipInterface -and $adapter.Status -ne 'Disabled') {
            return
        }
    }

    throw "Adapter '$Name' did not come back within 30 seconds."
}

function Get-PersistedIp([string]$Name) {
    # The registry is what Windows loads at boot, so it shows whether the address survives a restart.
    # An interface without a stored address holds an empty string here, not an empty list.
    $guid = (Get-NetAdapter -Name $Name).InterfaceGuid
    $settings = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guid" -ErrorAction SilentlyContinue
    if (-not $settings -or $settings.EnableDHCP -ne 0) { return }
    $settings.IPAddress | Where-Object { $_ }
}

function Show-Adapter([string]$Name) {
    $adapter = Get-NetAdapter -Name $Name
    Write-Output ''
    Write-Output "Camera adapter: $($adapter.Name) - $($adapter.InterfaceDescription)"

    Write-Check ($adapter.Status -eq 'Up') "Link: $($adapter.Status) (not Up = camera unplugged, or no PoE power)"
    Write-Check ($adapter.ReceiveLinkSpeed -ge 1000000000) "Speed: $($adapter.LinkSpeed) (below 1 Gbps = check the cable)"

    $jumbo = Get-AdvancedProperty $Name '*JumboPacket'
    $jumboValue = Get-RegistryNumber $jumbo
    Write-Check ($null -ne $jumboValue -and $jumboValue -ge 9000) "Jumbo frames: $(if ($jumbo) { $jumbo.DisplayValue } else { 'not available' })"

    $buffers = Get-AdvancedProperty $Name '*ReceiveBuffers'
    if ($buffers) {
        $max = [int]$buffers.NumericParameterMaxValue
        Write-Check ((Get-RegistryNumber $buffers) -ge $max) "Receive buffers: $($buffers.DisplayValue) (max $max)"
    }

    $ip = Get-NetIPConfiguration -InterfaceAlias $Name
    $addresses = @($ip.IPv4Address | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" }) -join ', '
    $dhcp = (Get-NetIPInterface -InterfaceAlias $Name -AddressFamily IPv4).Dhcp
    Write-Check ($dhcp -eq 'Disabled') "IPv4: $addresses (DHCP $dhcp; static expected)"
    $persisted = @(Get-PersistedIp $Name)
    Write-Check ($persisted.Count -gt 0) "Stored for the next boot: $(if ($persisted) { $persisted -join ', ' } else { 'nothing, the address is lost on restart' })"
    Write-Check ($null -eq $ip.IPv4DefaultGateway) "Gateway: $(if ($ip.IPv4DefaultGateway) { $ip.IPv4DefaultGateway.NextHop } else { 'none' }) (none expected)"

    $pylon = @(Get-NetAdapterBinding -Name $Name -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'pylon|Basler' })
    if ($pylon.Count -gt 0) {
        foreach ($binding in $pylon) {
            Write-Check $binding.Enabled "pylon driver binding: $($binding.DisplayName) (enabled: $($binding.Enabled))"
        }
    }
    else {
        Write-Check $false 'pylon GigE driver: no filter binding (install it from the pylon setup; a Performance driver would replace the adapter driver instead)'
    }

    Write-Output "       Adapter driver: $($adapter.DriverProvider) $($adapter.DriverVersion)"
    $rule = Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue
    Write-Check ($null -ne $rule) "Firewall rule '$FirewallRuleName': $(if ($rule) { 'present' } else { 'missing' })"
}

$physical = @(Get-NetAdapter -Physical)
Write-Output 'Physical adapters:'
foreach ($candidate in $physical) {
    $gateway = (Get-NetIPConfiguration -InterfaceAlias $candidate.Name).IPv4DefaultGateway
    $role = if ($gateway) { 'has a gateway (internet)' } else { 'no gateway' }
    Write-Output "  $($candidate.Name) - $($candidate.InterfaceDescription) - $($candidate.Status), $($candidate.LinkSpeed), $role"
}

if (-not $AdapterName) {
    $i210 = @($physical | Where-Object { $_.InterfaceDescription -match 'I210' })
    if ($i210.Count -ne 1) {
        Write-Output ''
        Write-Output "Found $($i210.Count) Intel I210 adapters. Install the card, or pass -AdapterName <name>."
        if ($Apply) { exit 1 }
        exit 0
    }

    $AdapterName = $i210[0].Name
}

if ($Apply) {
    if ((Get-NetIPConfiguration -InterfaceAlias $AdapterName).IPv4DefaultGateway) {
        Write-Output ''
        Write-Output "REFUSED: '$AdapterName' has a default gateway, so it is this PC's network connection, not the camera adapter. Nothing was changed."
        exit 1
    }

    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $WhatIfPreference -and -not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Output ''
        Write-Output 'REFUSED: -Apply needs an elevated PowerShell (Run as administrator). Nothing was changed.'
        exit 1
    }

    # Writing an advanced property restarts the adapter, so skip values that are already set.
    if ((Get-RegistryNumber (Get-AdvancedProperty $AdapterName '*JumboPacket')) -ne $JumboPacket -and $PSCmdlet.ShouldProcess($AdapterName, "Set jumbo frames to $JumboPacket")) {
        Set-NetAdapterAdvancedProperty -Name $AdapterName -RegistryKeyword '*JumboPacket' -RegistryValue $JumboPacket
    }

    $buffers = Get-AdvancedProperty $AdapterName '*ReceiveBuffers'
    if ($buffers -and (Get-RegistryNumber $buffers) -lt [int]$buffers.NumericParameterMaxValue -and $PSCmdlet.ShouldProcess($AdapterName, "Set receive buffers to $($buffers.NumericParameterMaxValue)")) {
        Set-NetAdapterAdvancedProperty -Name $AdapterName -RegistryKeyword '*ReceiveBuffers' -RegistryValue ([string]$buffers.NumericParameterMaxValue)
    }

    if ($PSCmdlet.ShouldProcess($AdapterName, "Set static IPv4 $HostIp/$PrefixLength without a gateway")) {
        Wait-AdapterReady $AdapterName
        # New-NetIPAddress left the address in the active store only and failed on the persistent one
        # ("Inconsistent parameters PolicyStore PersistentStore and Dhcp Enabled"). netsh does both in one step.
        Get-NetIPAddress -InterfaceAlias $AdapterName -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
        $index = (Get-NetAdapter -Name $AdapterName).ifIndex
        $netshOutput = & netsh interface ipv4 set address "name=$index" source=static "address=$HostIp/$PrefixLength" gateway=none store=persistent
        if (@(Get-PersistedIp $AdapterName) -notcontains $HostIp) {
            throw "Could not store $HostIp on '$AdapterName'. netsh said: $netshOutput"
        }
    }

    if ($PSCmdlet.ShouldProcess($AdapterName, "Add inbound UDP firewall rule '$FirewallRuleName' for this interface")) {
        Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $FirewallRuleName -Direction Inbound -Protocol UDP -Action Allow -InterfaceAlias $AdapterName -Profile Any | Out-Null
    }

    if (-not $WhatIfPreference) {
        Write-Output 'Applied; the adapter may take a few seconds to come back up.'
        Start-Sleep -Seconds 5
    }
}

Show-Adapter $AdapterName
Write-Output ''
Write-Output "Next: in pylon IP Configurator give the camera a persistent IP in the same subnet (e.g. 192.168.10.2/$PrefixLength)."
