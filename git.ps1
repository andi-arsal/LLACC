# Menyiapkan repositori Git (bila belum ada), commit, dan push (opsional).
#   powershell -ExecutionPolicy Bypass -File git.ps1
#   powershell -ExecutionPolicy Bypass -File git.ps1 -Message "Perbaiki dekoder"
#   powershell -ExecutionPolicy Bypass -File git.ps1 -Remote https://github.com/USER/LLACC.git -Push
param(
  [string]$Message,
  [string]$Remote,
  [string]$Branch = 'main',
  [switch]$Push,
  [switch]$Yes
)
$ErrorActionPreference = 'Stop'

function Invoke-Git {
  & git @args
  if ($LASTEXITCODE -ne 0) { throw "Perintah gagal: git $args" }
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "Git belum terpasang atau tidak ada di PATH." }

Push-Location -LiteralPath $PSScriptRoot
try {
  if (-not (Test-Path -LiteralPath '.git')) {
    Invoke-Git init
    # Cara ini juga berlaku di Git lama yang belum mengenal "init -b"
    Invoke-Git symbolic-ref HEAD "refs/heads/$Branch"
  }

  if (-not (git config user.name) -or -not (git config user.email)) {
    throw 'Identitas Git belum diatur. Jalankan: git config --global user.name "Nama"  dan  git config --global user.email "email"'
  }

  if ($Remote) {
    if (@(git remote) -contains 'origin') { Invoke-Git remote set-url origin $Remote }
    else { Invoke-Git remote add origin $Remote }
  }

  Invoke-Git add --all
  & git diff --cached --quiet
  if ($LASTEXITCODE -eq 0) {
    Write-Host "Tidak ada perubahan untuk di-commit."
  } else {
    Write-Host "`nFile yang akan di-commit:"
    Invoke-Git status --short

    & git rev-parse --verify --quiet HEAD | Out-Null
    $first = $LASTEXITCODE -ne 0

    if (-not $Message) {
      if ($first) { $Message = 'Commit awal LLACC' }
      else { $Message = Read-Host "Pesan commit" }
    }
    if (-not $Message.Trim()) { throw "Pesan commit kosong." }

    if (-not $Yes -and (Read-Host "Lanjutkan commit? (y/n)") -ne 'y') {
      Write-Host "Dibatalkan. Perubahan tetap berada di staging."
      return
    }
    Invoke-Git commit -m $Message
  }

  if ($Push) {
    if (@(git remote) -notcontains 'origin') { throw "Remote 'origin' belum ada. Gunakan -Remote <url>." }
    $current = git symbolic-ref --short HEAD
    Invoke-Git push -u origin $current
  }

  Write-Host ""
  Invoke-Git status --short --branch
}
finally {
  Pop-Location
}
