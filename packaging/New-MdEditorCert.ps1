<#
.SYNOPSIS
    Create a self-signed code-signing certificate for MSIX packaging of md-editor.

.DESCRIPTION
    Generates a code-signing certificate whose Subject (CN, O, C) matches the
    Publisher attribute in Package.appxmanifest. Exports a password-protected
    .pfx (used by signtool) and a public .cer (which end users import into
    their Trusted People store so the MSIX installs without warnings).

    The certificate uses:
      - 2048-bit RSA
      - SHA256 signature algorithm
      - Code Signing EKU (1.3.6.1.5.5.7.3.3) - required by MSIX
      - 3-year validity (override with -ValidityYears)

.PARAMETER Subject
    The X.500 distinguished name. MUST exactly match the Publisher in
    Package.appxmanifest, otherwise the install will fail with 0x800B0109.

.PARAMETER OutputDirectory
    Directory to place the .pfx / .cer (default: ./certs next to this script).

.PARAMETER ValidityYears
    Years until certificate expiry (default: 3).

.PARAMETER Password
    SecureString password for the .pfx. If omitted, the script prompts.

.EXAMPLE
    # Create with the default publisher
    .\New-MdEditorCert.ps1

.EXAMPLE
    # Create with a custom publisher and custom validity
    .\New-MdEditorCert.ps1 -Subject 'CN=Acme Corp, O=Acme, C=SE' -ValidityYears 1

.EXAMPLE
    # Non-interactive: pipe a password in
    $pw = ConvertTo-SecureString 'changeme!' -AsPlainText -Force
    .\New-MdEditorCert.ps1 -Password $pw

.NOTES
    After running, install the .cer into Cert:\LocalMachine\TrustedPeople
    (requires admin) so the signed MSIX trusts on this machine:

        Import-Certificate -FilePath .\certs\md-editor.cer `
            -CertStoreLocation Cert:\LocalMachine\TrustedPeople
#>

[CmdletBinding()]
param(
    [string]$Subject = 'CN=MD Editor Dev, O=MD Editor, C=SE',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'certs'),

    [int]$ValidityYears = 3,

    [SecureString]$Password
)

function New-MdEditorCert {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Subject,
        [Parameter(Mandatory)] [string]$OutputDirectory,
        [Parameter(Mandatory)] [int]$ValidityYears,
        [Parameter(Mandatory)] [SecureString]$Password
    )

    if (-not (Test-Path $OutputDirectory)) {
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    }

    Write-Host "Generating self-signed code-signing certificate..." -ForegroundColor Cyan
    Write-Host "  Subject:    $Subject"
    Write-Host "  Valid for:  $ValidityYears years"
    Write-Host "  Output dir: $OutputDirectory"

    $cert = New-SelfSignedCertificate `
        -Subject $Subject `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears($ValidityYears) `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')

    if (-not $cert) {
        throw "New-SelfSignedCertificate returned no certificate. Are you running with the right permissions?"
    }

    Write-Host "Certificate created. Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green

    $pfxPath = Join-Path $OutputDirectory 'md-editor.pfx'
    $cerPath = Join-Path $OutputDirectory 'md-editor.cer'

    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $Password | Out-Null
    Export-Certificate    -Cert $cert -FilePath $cerPath -Type CERT       | Out-Null

    Write-Host "Exported:" -ForegroundColor Green
    Write-Host "  .pfx (signing):  $pfxPath"
    Write-Host "  .cer (trust):    $cerPath"

    [PSCustomObject]@{
        Thumbprint = $cert.Thumbprint
        Subject    = $cert.Subject
        NotAfter   = $cert.NotAfter
        PfxPath    = $pfxPath
        CerPath    = $cerPath
    }
}

# --- Driver: collect the password (or prompt) then call the function ---

if (-not $Password) {
    $Password = Read-Host -Prompt 'PFX password' -AsSecureString
    $confirm  = Read-Host -Prompt 'Confirm password' -AsSecureString

    $a = [System.Net.NetworkCredential]::new('', $Password).Password
    $b = [System.Net.NetworkCredential]::new('', $confirm).Password
    if ($a -ne $b) {
        Write-Error "Passwords did not match. Aborting."
        exit 1
    }
}

$result = New-MdEditorCert `
    -Subject $Subject `
    -OutputDirectory $OutputDirectory `
    -ValidityYears $ValidityYears `
    -Password $Password

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow

$nextSteps = @"
  1. Trust the cert on this machine (admin):
     Import-Certificate -FilePath '$($result.CerPath)' -CertStoreLocation Cert:\LocalMachine\TrustedPeople

  2. Sign the MSIX:
     signtool sign /fd SHA256 /f '$($result.PfxPath)' /p <password> md-editor.msix

  3. Verify the Publisher in Package.appxmanifest matches:
     Identity Publisher = "$Subject"
"@

Write-Host $nextSteps
