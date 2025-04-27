#!/bin/bash

# === tunneling ===
FOLDER_TUNNEL="tunnel"
URLS_TUNNEL=(
    "https://github.com/jpillora/chisel/releases/download/v1.10.1/chisel_1.10.1_windows_amd64.gz"
    "https://github.com/jpillora/chisel/releases/download/v1.10.1/chisel_1.10.1_linux_amd64.gz"
)

# === Privesc ===
FOLDER_PRIVESC="privesc"
URLS_PRIVESC=(
    "https://github.com/dievus/printspoofer/raw/refs/heads/master/PrintSpoofer.exe"
    "https://raw.githubusercontent.com/PowerShellEmpire/PowerTools/refs/heads/master/PowerUp/PowerUp.ps1"
    "https://github.com/DominicBreuker/pspy/releases/download/v1.2.1/pspy32"
    "https://github.com/DominicBreuker/pspy/releases/download/v1.2.1/pspy64"
    "https://github.com/ohpe/juicy-potato/releases/download/v0.1/JuicyPotato.exe"
    "https://github.com/ParrotSec/mimikatz/blob/master/x64/mimikatz.exe"
)

# === Tools Section ===
FOLDER_ENUM="enumeration"
URLS_ENUM=(
    "https://githubusercontent.com/PowerShellMafia/PowerSploit/refs/heads/master/Recon/PowerView.ps1"
    "https://github.com/61106960/adPEAS/blob/main/adPEAS.ps1"
    "https://github.com/peass-ng/PEASS-ng/releases/download/20250424-d80957fb/linpeas.sh"
    "https://github.com/peass-ng/PEASS-ng/releases/download/20250424-d80957fb/winPEASx86.exe"
    "https://pentestmonkey.net/tools/unix-privesc-check/unix-privesc-check-1.4.tar.gz"
    "https://github.com/SpecterOps/SharpHound/releases/download/v2.6.5/SharpHound_v2.6.5_windows_x86.zip"

)

# === Tools Section ===
FOLDER_SHELLS="shells"
URLS_SHELLS=(
   "https://raw.githubusercontent.com/besimorhino/powercat/refs/heads/master/powercat.ps1"
   "https://github.com/int0x33/nc.exe/raw/refs/heads/master/nc64.exe"
)

# --- Function to create folder and download URLs ---
function download_section() {
    local folder="$1"
    shift
    local urls=("$@")

    echo "Creating folder: $folder"
    mkdir -p "$folder"

    echo "Downloading files into $folder..."
    for url in "${urls[@]}"; do
        echo "Downloading $url"
        wget -P "$folder" "$url"
    done
}

# --- Execute each section ---

# Github Scripts

download_section "$FOLDER_TUNNEL" "${URLS_TUNNEL[@]}"

# Personal Projects

download_section "$FOLDER_PRIVESC" "${URLS_PRIVESC[@]}"

# Tools

download_section "$FOLDER_ENUM" "${URLS_ENUM[@]}"

download_section "$FOLDER_SHELLS" "${URLS_SHELLS[@]}"


# === Add New Sections Below ===
# Example:
# FOLDER_NEW="new_folder"
# URLS_NEW=(
#     "https://example.com/newfile1"
#     "https://example.com/newfile2"
# )
# download_section "$FOLDER_NEW" "${URLS_NEW[@]}"
