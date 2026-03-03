
# 📸 Syntry Face Recognition System

**Windows Server · Ubuntu 24.04 LTS · PostgreSQL · WebSocket · .NET 8**

A real-time Face Recognition & Attendance System built using:

- YuNet (Face Detection)
- SFace (Face Recognition)
- MiniFASNet (Anti-Spoofing)
- WebSocket (SuperSocket)
- PostgreSQL
- .NET 8
- Emgu CV (OpenCV)

---

# 🧠 System Architecture

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
    PostgreSQL Database
        |
    Admin Client

---

# ✅ Supported Platforms

| Platform | Status |
|----------|--------|
| Windows 10 / 11 | Supported |
| Ubuntu 24.04 LTS | Supported |
| Ubuntu 22.04 | Not Supported |
| AlmaLinux / RHEL | Not Supported |

Linux must be Ubuntu 24.04 LTS due to EmguCV native dependency requirements.

---

# 🖥 Server Requirements

## Hardware
- x64 CPU
- Minimum 4GB RAM (8GB recommended)
- SSD recommended

## Software
- .NET 8 Runtime
- Emgu.CV native runtime
- PostgreSQL 16+
- Required Linux native libraries

---

# Windows Server Setup

## Install .NET 8 Runtime
https://dotnet.microsoft.com/download/dotnet/8.0

Verify:
dotnet --info

## Configure appsettings.json

{
  "ConnectionStrings": {
    "Default": "Host=127.0.0.1;Port=5432;Database=db_fb;Username=syntry_user;Password=StrongPassword123!"
  }
}

## Run Server

dotnet CloudDemoNet8.dll 9010

---

# Ubuntu 24.04 Server Setup

## Install .NET 8

sudo apt update
sudo apt install -y dotnet-host-8.0 dotnet-runtime-8.0

Verify:
dotnet --info

## Install Required Native Libraries

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

---

# PostgreSQL Setup

sudo apt install -y postgresql
sudo systemctl enable postgresql
sudo systemctl start postgresql

sudo -u postgres psql

CREATE DATABASE db_fb;

CREATE USER syntry_user WITH PASSWORD 'StrongPassword123!';

GRANT ALL PRIVILEGES ON DATABASE db_fb TO syntry_user;

\c db_fb

CREATE TABLE tblusers_face (
    enrollid      INTEGER PRIMARY KEY,
    username      TEXT,
    backupnum     INTEGER,
    admin         BOOLEAN,
    record        TEXT,
    regdattime    TIMESTAMP,
    isactive      BOOLEAN
);

CREATE TABLE tblattendance_face (
    enrollid      INTEGER,
    device        TEXT,
    attendattime  TIMESTAMP
);

CREATE INDEX idx_attendance_enroll_time
ON tblattendance_face (enrollid, attendattime DESC);

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO syntry_user;

---

# Production Run (PM2)

sudo apt install -y npm
sudo npm install -g pm2

Single instance:
pm2 start dotnet --name syntry -- CloudDemoNet8-Linux-EMGU.dll 9010

Multiple instances:
pm2 start dotnet --name syntry-1 -- CloudDemoNet8-Linux-EMGU.dll 9010
pm2 start dotnet --name syntry-2 -- CloudDemoNet8-Linux-EMGU.dll 9011

pm2 save
pm2 startup

---

# Networking Notes

If running inside VM:
- Use Bridged Adapter
- Ensure required ports are opened in firewall or security group

---

# Known Limitations

Ubuntu 22.04 not supported due to older glibc.
AlmaLinux / RHEL not supported due to ABI mismatch.
net8.0-windows target not supported on Linux.

---

# License

Internal / Private Deployment Only
