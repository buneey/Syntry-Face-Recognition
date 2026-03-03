# 📸 Syntry Face Recognition System

**Windows Server · Ubuntu 24.04.4 LTS · PostgreSQL · WebSocket · .NET
8**

A real-time Face Recognition & Attendance System built using:

-   YuNet (Face Detection)
-   SFace (Face Recognition)
-   MiniFASNet (Anti-Spoofing)
-   WebSocket (SuperSocket)
-   PostgreSQL
-   .NET 8
-   Emgu CV (OpenCV)

------------------------------------------------------------------------

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

------------------------------------------------------------------------

# ✅ Supported Platforms

  Platform           Status
  ------------------ ---------------
  Windows 11    Supported
  Ubuntu 24.04 LTS   Supported
  Ubuntu 22.04       Not Supported
  AlmaLinux / RHEL   Not Supported

Ubuntu 24.04 is required due to EmguCV native dependency compatibility.

------------------------------------------------------------------------

# 🐧 Ubuntu 24.04.4 Server LTS Installation (Bridged Network VM)

> VM must use **Bridged Network** if devices are on the same LAN.

## 1️⃣ Initial Server Setup

``` bash
sudo apt update && sudo apt upgrade -y
sudo apt install git -y
sudo reboot
```

Clone repository:

``` bash
git clone https://github.com/buneey/Syntry-Face-Recognition
mv "Syntry-Face-Recognition/Server-Client-Ready/Server - Linux - EMGU" ./SyntryServer
rm -r Syntry-Face-Recognition
cd SyntryServer
```

------------------------------------------------------------------------

## 2️⃣ Install .NET 8

``` bash
sudo apt install -y dotnet-sdk-8.0
dotnet --info
```

------------------------------------------------------------------------

## 3️⃣ Install Required Native Libraries

``` bash
sudo apt install -y   libgtk2.0-0t64   libgeotiff5   libjpeg8   libpng16-16t64   libopenjp2-7   libavcodec60   libavformat60   libavutil58   libswscale7   liblapack3   libblas3   libhdf5-103-1t64   ffmpeg   libvtk9.1t64
```

Verify OpenCV:

``` bash
ldd libcvextern.so | grep "not found"
```

Expected: No output.

### Optional (if required)

``` bash
sudo apt install -y   libgstreamer1.0-0   libgstreamer-plugins-base1.0-0   gstreamer1.0-plugins-base   gstreamer1.0-plugins-good   gstreamer1.0-plugins-bad   gstreamer1.0-plugins-ugly   gstreamer1.0-libav
```

------------------------------------------------------------------------

## 4️⃣ Test Server

``` bash
dotnet CloudDemoNet8-Linux-EMGU.dll
```

------------------------------------------------------------------------

# 🗄 PostgreSQL Setup

Install:

``` bash
sudo apt install postgresql postgresql-contrib -y
sudo systemctl enable postgresql
sudo systemctl start postgresql
sudo -i -u postgres
psql
```

Create database and user:

``` sql
CREATE DATABASE db_fb;
CREATE USER username WITH PASSWORD 'password';
GRANT ALL PRIVILEGES ON DATABASE db_fb TO username;
\q
exit
```

Connect:

``` bash
psql -U username -d db_fb -h localhost
```

Create tables:

``` sql
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
```

### If Permission Denied

``` bash
sudo -i -u postgres
psql
ALTER DATABASE db_fb OWNER TO username;
GRANT ALL ON SCHEMA public TO username;
ALTER SCHEMA public OWNER TO username;
\q
exit
```

Then retry table creation.

------------------------------------------------------------------------

## PostgreSQL Port Change (Optional)

``` bash
sudo find /etc/postgresql -name postgresql.conf
sudo nano /etc/postgresql/16/main/postgresql.conf
sudo systemctl restart postgresql
```

Correct connection string:

``` json
"ConnectionStrings": {
  "Default": "Host=127.0.0.1;Port=5432;Database=db_fb;Username=username;Password=yourpassword"
}
```

------------------------------------------------------------------------

# 🚀 PM2 Production Setup

Install Node & PM2:

``` bash
sudo apt update
sudo apt install nodejs npm -y
sudo npm install -g pm2
pm2 -v
```

Run server:

``` bash
pm2 start "dotnet CloudDemoNet8-Linux-EMGU.dll 9010" --name syntry-1
```

Commands:

``` bash
pm2 list
pm2 logs syntry-1
pm2 restart syntry-1
pm2 stop syntry-1
pm2 delete syntry-1
```

Auto-start on reboot:

``` bash
pm2 startup
# Run the command it provides
pm2 save
```

------------------------------------------------------------------------

# 🌐 Find Server IP

``` bash
ip a
```

------------------------------------------------------------------------

# ⚠ Known Issues

-   Ubuntu 22.04 not supported (glibc too old)
-   AlmaLinux / RHEL not supported (ABI mismatch)
-   net8.0-windows target not compatible with Linux runtime
