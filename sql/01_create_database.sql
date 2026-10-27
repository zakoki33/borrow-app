/* ===========================================================
   ระบบยืม-คืน Master Sample : สร้างฐานข้อมูล MS SQL Server
   วิธีใช้: เปิดใน SSMS > เลือกทั้งหมด > กด Execute (F5)
   สคริปต์รันซ้ำได้ ไม่ทำให้ข้อมูลเดิมหาย
   =========================================================== */

-- 1) ฐานข้อมูล (ตั้งชื่อเป็นภาษาอังกฤษ เพื่อกันปัญหากับเครื่องมือต่าง ๆ)
IF DB_ID(N'MasterSampleDB') IS NULL
    CREATE DATABASE MasterSampleDB COLLATE Thai_CI_AS;
GO

USE MasterSampleDB;
GO

-- 2) ตารางบุคคล (ตรงกับชีต "รายชื่อ")
IF OBJECT_ID(N'dbo.Persons') IS NULL
CREATE TABLE dbo.Persons (
    PersonId   NVARCHAR(20)  NOT NULL CONSTRAINT PK_Persons PRIMARY KEY,
    FullName   NVARCHAR(200) NOT NULL,
    Department NVARCHAR(100) NULL,
    Phone      NVARCHAR(20)  NULL,
    IsActive   BIT           NOT NULL CONSTRAINT DF_Persons_Active DEFAULT 1
);
GO

-- 3) ตารางอุปกรณ์/Part (ตรงกับชีต "รายการอุปกรณ์")
IF OBJECT_ID(N'dbo.Parts') IS NULL
CREATE TABLE dbo.Parts (
    PartId   NVARCHAR(30)  NOT NULL CONSTRAINT PK_Parts PRIMARY KEY,
    Name     NVARCHAR(200) NOT NULL,
    Type     NVARCHAR(100) NULL,
    Location NVARCHAR(100) NULL,
    IsActive BIT           NOT NULL CONSTRAINT DF_Parts_Active DEFAULT 1
);
GO

-- 4) ตารางประวัติยืม-คืน (ตรงกับชีต "ประวัติการยืมคืน")
--    ไม่มีคอลัมน์ "สถานะ": ถ้า ReturnedAt ว่าง = ยังไม่คืน
IF OBJECT_ID(N'dbo.Loans') IS NULL
CREATE TABLE dbo.Loans (
    LoanId     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Loans PRIMARY KEY,
    PartId     NVARCHAR(30) NOT NULL CONSTRAINT FK_Loans_Parts   FOREIGN KEY REFERENCES dbo.Parts(PartId),
    PersonId   NVARCHAR(20) NOT NULL CONSTRAINT FK_Loans_Persons FOREIGN KEY REFERENCES dbo.Persons(PersonId),
    StartDate  DATE         NOT NULL,
    DueDate    DATE         NOT NULL,
    ReturnedAt DATETIME2(0) NULL,
    CONSTRAINT CK_Loans_Dates CHECK (DueDate >= StartDate)
);
GO

-- Part 1 ชิ้นมีรายการ "ยังไม่คืน" ได้ไม่เกิน 1 รายการ (กันยืมซ้ำ/กดพร้อมกัน)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Loans_OpenPart')
    CREATE UNIQUE INDEX UX_Loans_OpenPart ON dbo.Loans(PartId) WHERE ReturnedAt IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loans_Person')
    CREATE INDEX IX_Loans_Person ON dbo.Loans(PersonId);
GO

-- 5) View สถานะอุปกรณ์ (ใช้กับ Dashboard: พร้อมให้ยืม / ถูกยืม / เลยกำหนด)
CREATE OR ALTER VIEW dbo.vPartStatus AS
SELECT
    p.PartId, p.Name, p.Type, p.Location,
    CASE WHEN l.LoanId IS NULL THEN N'พร้อมให้ยืม' ELSE N'ถูกยืม' END AS Status,
    l.PersonId   AS BorrowerId,
    pe.FullName  AS BorrowerName,
    l.StartDate, l.DueDate,
    CASE WHEN l.DueDate < CAST(GETDATE() AS DATE) THEN 1 ELSE 0 END AS IsOverdue
FROM dbo.Parts p
LEFT JOIN dbo.Loans   l  ON l.PartId = p.PartId AND l.ReturnedAt IS NULL
LEFT JOIN dbo.Persons pe ON pe.PersonId = l.PersonId
WHERE p.IsActive = 1;
GO

-- 6) ข้อมูลตัวอย่าง (ลบส่วนนี้ได้ ถ้าจะนำเข้าข้อมูลจริงเอง)
IF NOT EXISTS (SELECT 1 FROM dbo.Persons)
INSERT INTO dbo.Persons (PersonId, FullName, Department, Phone) VALUES
    (N'20261025', N'กิตติศักดิ์', N'OSM', N'0917215502'),
    (N'20261125', N'A',           N'OSM', N'0917215503');

IF NOT EXISTS (SELECT 1 FROM dbo.Parts)
INSERT INTO dbo.Parts (PartId, Name, Type, Location) VALUES
    (N'M0001', N'A-LH-9233', N'WELD OK', N'OSM'),
    (N'M0002', N'A-RH-9233', N'WELD OK', N'OSM'),
    (N'M0003', N'B-LH-9233', N'WELD NG', N'OSM'),
    (N'M0004', N'B-RH-9233', N'WELD NG', N'OSM'),
    (N'M0005', N'C-LH-9235', NULL,       N'OSM'),
    (N'M0006', N'C-RH-9235', NULL,       N'OSM');
GO

-- 7) ตรวจผล
SELECT * FROM dbo.vPartStatus ORDER BY PartId;
SELECT * FROM dbo.Persons;
GO
