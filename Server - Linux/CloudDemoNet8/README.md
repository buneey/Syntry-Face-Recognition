# Syntry – Face Recognition & Attendance System

## Overview

**Syntry** is a .NET 8 WebSocket-based face recognition and attendance system designed for biometric access control and attendance tracking.

It consists of:
- A **high-performance server** responsible for face recognition, anti-spoofing, persistence, and device communication
- A **console-based Admin Client** used by operators to manage users, devices, and monitor live recognition events

The system is designed for **on-premise / LAN deployments** and supports **multiple biometric devices, multiple server ports, and concurrent instances** sharing a single SQL Server database.

---

## System Components

### 1. Syntry Server
- Handles biometric device communication
- Performs face recognition and liveness detection
- Manages user state and persistence
- Acts as the system source of truth

### 2. Syntry Admin Client
- Operator console UI
- Manages users and devices
- Initiates enrollment
- Displays live recognition events
- Supports dynamic server port switching

---

## Core Features

### Server
- WebSocket server for biometric terminals
- Face recognition using Emgu CV (OpenCV)
- Anti-spoofing (liveness detection) using DNN
- SQL Server as the single source of truth
- Database-level EnrollID sequence (no ID reuse)
- High-performance in-memory face embedding cache
- Periodic DB ↔ RAM synchronization
- Safe WebSocket lifecycle handling
- Multi-port / multi-instance safe operation

### Admin Client
- WebSocket-based admin control channel
- Console UI built with Spectre.Console
- User enrollment initiation
- User deletion and activation control
- Device discovery and selection
- Live face recognition monitoring
- Dynamic server port switching
- Automatic admin session re-registration

---

## High-Level Architecture

```
Biometric Device
       │
       ▼
 WebSocketLoader  ───▶  FaceMatch (AI + RAM Cache)
       │                     │
       ▼                     ▼
 Admin Client           SQL Server (Source of Truth)
```

---

## Database Model

### `tblusers`
- `enrollid` (globally unique, immutable)
- `username`
- `record` (base64 face image)
- `backupnum`
- `isactive`

### `tblattendance`
- `enrollid`
- `device`
- `attendattime`

---

## EnrollID Generation

Enroll IDs are generated using a **SQL Server sequence**:

```sql
SELECT NEXT VALUE FOR dbo.EnrollIdSeq;
```

---

## Enrollment Flow

1. Operator starts enrollment from the Admin Client
2. Server generates a new EnrollID
3. Device captures face images
4. Face data is stored in SQL Server
5. Face embedding is added to RAM
6. User is immediately available for recognition

---

## Recognition Flow

1. Device sends face image
2. Anti-spoofing (liveness detection) is executed
3. Face features are extracted
4. In-memory matching is performed
5. Access decision is made
6. Attendance is logged only on success

---

## Synchronization Model

- Periodic background sync keeps RAM consistent with database
- Supports multiple servers and ports
- Ensures zero-downtime updates

---

## Configuration

Configured via `appsettings.json`.

---

## Summary

Syntry provides a complete, modular, and production-capable biometric attendance system with strong identity guarantees, safe concurrency, and operator-friendly administration.
