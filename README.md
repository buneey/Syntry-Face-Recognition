# 📸 Syntry Face Recognition System
Windows Server · Linux Server (Ubuntu 24.04 LTS) · Client Devices

A real-time **face recognition & attendance system** built using:
- YuNet (Face Detection)
- SFace (Face Recognition)
- MiniFASNet (Anti-Spoofing)
- WebSocket communication
- MySQL or SQL Server
- .NET 8

---

## 🧠 System Architecture

[ Face Device ]
      |
   WebSocket
      |
[ Syntry Server ]
  - Face Detection (YuNet)
  - Face Recognition (SFace)
  - Anti-Spoofing
  - Attendance Logging
      |
   Database (MySQL / SQL Server)

---

## ✅ Supported Platforms

| Platform | Status |
|--------|--------|
| Windows 10 / 11 | ✅ Supported |
| Ubuntu 24.04 LTS | ✅ Supported |
| Ubuntu 22.04 | ❌ Not Supported |
| AlmaLinux 9 / 10 | ❌ Not Supported |

> ⚠️ Linux **must be Ubuntu 24.04 LTS**  
> Older distros fail due to OpenCV + glibc native dependency issues.

---

# 🖥️ SERVER REQUIREMENTS

### Hardware
- x64 CPU
- Minimum 4 GB RAM (8 GB recommended)
- SSD storage

### Software
- .NET 8 Runtime
- Emgu.CV (OpenCV)
- MySQL or SQL Server
- Native Linux libraries (Linux only)

---

# 🪟 WINDOWS SERVER SETUP

## 1️⃣ Install .NET 8 Runtime
Download:
https://dotnet.microsoft.com/download/dotnet/8.0

Verify:
dotnet --info

Configure appsettings.json

Run the Server:
Server_Start_7790

# LINUX SERVER SETUP
UBUNUTU 24.04 LTS / BRIDGED NETWORK for VM

1)
sudo apt update
sudo apt install -y dotnet-host-8.0 dotnet-runtime-8.0
or
sudo apt install -y dotnet-sdk-8.0

Verify:
dotnet --info
Expected:
.NET SDKs installed:
.NET runtimes installed:
Microsoft.NETCore.App 8.0.x


2)
sudo apt install -y \
  libgtk2.0-0t64 \
  libgeotiff5 \
  libjpeg8 \
  libpng16-16t64 \
  libopenjp2-7 \
  libavcodec60 \
  libavformat60 \
  libavutil58 \
  libswscale7 \
  liblapack3 \
  libblas3 \
  libhdf5-103-1t64 \
  ffmpeg \
  libvtk9.1t64

Verify:
ldd libcvextern.so | grep "not found"
Expected:
*Nothing*

3) Test Server:

dotnet CloudDemoNet8-Linux-EMGU.dll

------------------------ MySQL Setup -------------------------------

sudo apt install -y mysql-server

sudo systemctl enable mysql

sudo systemctl start mysql

sudo mysql

CREATE DATABASE db_fb;
CREATE USER 'syntry_user'@'%' IDENTIFIED BY 'StrongPassword123!';
GRANT ALL PRIVILEGES ON db_fb.* TO 'syntry_user'@'%';
FLUSH PRIVILEGES;
EXIT;

mysql -u syntry_user -p db_fb


CREATE TABLE tblusers_face (
  enrollid INT NOT NULL PRIMARY KEY,
  username TEXT,
  backupnum INT,
  admin INT,
  record LONGTEXT,
  regdattime DATETIME,
  isactive INT
);

CREATE TABLE tblattendance_face (
  enrollid TEXT,
  device TEXT,
  attendattime DATETIME
);

FROM INSIDE THE SERVER FOLDER:
export LD_LIBRARY_PATH=$PWD:$PWD/runtimes/ubuntu-x64/native:$LD_LIBRARY_PATH

RUN Server:

dotnet CloudDemoNet8-Linux-EMGU.dll

To Find Server IP:
ip a

------------------------ RUNS THE SERVER ---------------------------

PROBLEMS:

❌ Ubuntu 22.04

glibc 2.35

EmguCV requires ≥ 2.38

Impossible without recompiling OpenCV

❌ AlmaLinux

Older glibc

Missing VTK / LAPACK ABI versions

Emgu runtime not built for RHEL ecosystem

❌ net8.0-windows

Forces Windows APIs

Breaks Linux runtime resolution
