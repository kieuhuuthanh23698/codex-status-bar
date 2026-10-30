# Initial Product Concept (Historical)

> This is the original planning brief. It contains proposed requirements and future ideas that may not be implemented in the current application. For current features, requirements, and build instructions, see the [project README](../README.md).

## 1. Mục tiêu

Xây dựng một ứng dụng Windows nhỏ chạy nền để theo dõi trạng thái sử dụng Codex và hiển thị trực tiếp trên khu vực Taskbar/System Tray.

Ứng dụng tập trung vào 3 thông tin chính:

- **Session usage**
- **Context usage**
- **Quota usage**

Ví dụ:

```text
CODEX | Session 72% | Context 38% | Quota 61%
```

---

## 2. Yêu cầu chính

### System Tray

Ứng dụng chạy nền và hiển thị icon tại khu vực System Tray của Windows.

Tooltip khi hover:

```text
Codex Status

Session: 72%
Context: 38%
5h Quota: 61%
Weekly Quota: 43%

5h Reset: 02:17:32
Weekly Reset: 3d 12h
```

---

### Floating Status Bar

Có thể bật một thanh nhỏ nằm sát Taskbar.

Ví dụ:

```text
┌─────────────────────────────────────────────┐
│ CODEX   S 72%   C 38%   5H 61%   W 43%    │
└─────────────────────────────────────────────┘
```

Thanh này:

- Borderless
- Always On Top
- Không xuất hiện trong Alt+Tab
- Có thể kéo vị trí
- Có thể khóa vị trí
- Có thể đặt sát Taskbar
- Tự ẩn khi fullscreen
- Có thể điều chỉnh opacity
- Hỗ trợ nhiều màn hình

---

## 3. Multi-Monitor Support

Ứng dụng phải hỗ trợ máy tính sử dụng nhiều màn hình.

Ví dụ máy có:

```text
Monitor 1 - Primary - 1920x1080
Monitor 2 - Secondary - 2560x1440
```

Người dùng có thể chọn nơi hiển thị Status Bar.

### Display Mode

```text
Show status bar on:

○ Primary monitor only
○ Monitor 1
○ Monitor 2
○ All monitors
```

Nếu có nhiều hơn 2 màn hình:

```text
☑ Monitor 1 - 1920x1080
☐ Monitor 2 - 2560x1440
☑ Monitor 3 - 1920x1080
```

Cho phép chọn một hoặc nhiều màn hình.

### All Monitors

Khi chọn:

```text
All monitors
```

mỗi màn hình sẽ có một Status Bar riêng.

Ví dụ:

```text
Monitor 1
────────────────────────────────
CODEX | S 72% | C 38% | Q 61%

Monitor 2
────────────────────────────────
CODEX | S 72% | C 38% | Q 61%
```

Dữ liệu giữa các Status Bar phải được đồng bộ.

---

## 4. Monitor Detection

Ứng dụng tự động phát hiện:

- Số lượng màn hình
- Primary monitor
- Resolution
- Working area
- Taskbar position
- DPI scaling

Ví dụ:

```text
Monitor 1
Primary: Yes
Resolution: 1920x1080
Scale: 100%

Monitor 2
Primary: No
Resolution: 2560x1440
Scale: 125%
```

Ứng dụng phải hỗ trợ Windows DPI Scaling độc lập giữa các màn hình.

---

## 5. Monitor Hot Plug

Ứng dụng phải xử lý trường hợp:

- Cắm thêm màn hình
- Rút màn hình
- Dock/undock laptop
- Thay đổi Primary monitor
- Thay đổi resolution
- Thay đổi DPI scaling

Ví dụ:

```text
Monitor 2 disconnected
```

Status Bar trên Monitor 2 sẽ tự đóng.

Nếu màn hình được kết nối lại, ứng dụng sẽ tự tạo lại Status Bar theo cấu hình trước đó.

---

## 6. Per-Monitor Position

Có thể cấu hình vị trí Status Bar riêng cho từng màn hình.

Ví dụ:

```text
Monitor 1
Position: Bottom Right

Monitor 2
Position: Top Right
```

Các vị trí hỗ trợ:

```text
Top Left
Top Center
Top Right

Bottom Left
Bottom Center
Bottom Right
```

Ngoài ra cho phép:

```text
Custom position
```

Người dùng có thể kéo Status Bar tới vị trí mong muốn.

Ứng dụng lưu lại vị trí riêng của từng màn hình.

---

## 7. Taskbar Awareness

Ứng dụng phải nhận biết vị trí Taskbar trên từng màn hình.

Taskbar có thể nằm:

```text
Bottom
Top
Left
Right
```

Status Bar phải tự căn vị trí để không che Taskbar.

Ví dụ:

```text
Taskbar: Bottom

Status Bar:
CODEX | S 72% | C 38% | Q 61%
────────────────────────────────
Windows Taskbar
```

---

## 8. Popup Dashboard

Click vào icon System Tray hoặc Status Bar để mở popup chi tiết.

Ví dụ:

```text
Codex Status
────────────────────

Current Session
Usage       72%
Context     38%

Quota

5 Hour
██████████████░░░░ 61%
Reset in 02:17:32

Weekly
██████████░░░░░░░░ 43%
Reset in 3d 12h

────────────────────
Last updated: 16:10:32
```

Popup nên xuất hiện trên màn hình chứa Status Bar mà người dùng vừa click.

---

## 9. Auto Refresh

Ứng dụng tự cập nhật dữ liệu định kỳ.

Mặc định:

```text
Refresh interval: 60 seconds
```

Cho phép cấu hình:

```text
15s
30s
60s
2m
5m
```

Có nút:

```text
Refresh Now
```

Tất cả Status Bar trên các màn hình phải được update cùng lúc.

---

## 10. Nguồn dữ liệu

Ưu tiên đọc thông tin từ dữ liệu mà Codex đang sử dụng trên máy local.

Thứ tự ưu tiên:

1. Local Codex session/state
2. Local usage/cache files
3. Codex CLI output
4. API chính thức nếu OpenAI cung cấp endpoint phù hợp

Không yêu cầu người dùng nhập trực tiếp:

- Password
- API key
- Access token

Không log credential hoặc dữ liệu nhạy cảm.

---

## 11. Codex CLI Integration

Nếu Codex CLI cung cấp dữ liệu phù hợp, ứng dụng có thể gọi các command như:

```bash
codex status
```

hoặc:

```bash
codex usage
```

Sau đó parse output thành model nội bộ.

Ví dụ:

```text
CodexUsageInfo
{
    SessionPercent
    ContextPercent
    FiveHourPercent
    WeeklyPercent
    FiveHourResetAt
    WeeklyResetAt
}
```

---

## 12. Cảnh báo Quota

Cho phép cấu hình cảnh báo.

Ví dụ:

```text
Quota > 70%
Quota > 85%
Quota > 95%
```

Khi đạt ngưỡng:

```text
Codex quota reached 85%

5-hour quota: 86%
Reset in: 01:12:40
```

Có thể cảnh báo bằng:

- Windows Toast Notification
- Đổi icon tray
- Đổi trạng thái Status Bar

---

## 13. Context Warning

Cảnh báo khi Context của session hiện tại gần đầy.

Ví dụ:

```text
Context > 80%
```

Thông báo:

```text
Codex Context Warning

Current context usage: 84%

Consider starting a new session.
```

---

## 14. Startup

Có tùy chọn:

```text
☑ Start Codex Status Bar with Windows
```

Ứng dụng khởi động minimized vào System Tray.

Sau khi startup, ứng dụng tự khôi phục:

- Monitor được chọn
- Vị trí Status Bar
- Display mode
- Opacity
- Visibility

---

## 15. Settings

Popup Settings:

```text
General

☑ Start with Windows
☑ Show tray icon
☑ Show floating status bar
☑ Always on top
☑ Hide when fullscreen

Refresh interval
[ 60 seconds ]


Display

Show on:

○ Primary monitor
○ Specific monitors
○ All monitors

Specific monitors:

☑ Monitor 1 - 1920x1080 - Primary
☐ Monitor 2 - 2560x1440

Display fields:

☑ Session
☑ Context
☑ 5 Hour Quota
☑ Weekly Quota

Opacity
[ 90% ]


Monitor 1

Position
[ Bottom Right ]

☑ Lock position


Monitor 2

Position
[ Bottom Right ]

☑ Lock position


Warnings

Context warning     [ 80% ]
Quota warning       [ 85% ]
Critical warning    [ 95% ]
```

---

## 16. Công nghệ

Đề xuất:

```text
.NET 8
C#
WPF
```

Ưu tiên **WPF** cho version đầu vì:

- Nhẹ
- Dễ làm System Tray
- Dễ tạo transparent/borderless window
- Dễ Always On Top
- Dễ xử lý multi-monitor
- Dễ xử lý DPI
- Dễ đóng gói thành single EXE
- Dễ maintain

---

## 17. Kiến trúc

```text
CodexStatusBar
│
├── App
│
├── Models
│   ├── CodexUsageInfo.cs
│   └── MonitorInfo.cs
│
├── Services
│   ├── CodexUsageService.cs
│   ├── CodexCliService.cs
│   ├── MonitorService.cs
│   ├── NotificationService.cs
│   ├── StartupService.cs
│   └── SettingsService.cs
│
├── Views
│   ├── StatusBarWindow.xaml
│   ├── DashboardWindow.xaml
│   └── SettingsWindow.xaml
│
├── ViewModels
│   ├── StatusBarViewModel.cs
│   ├── DashboardViewModel.cs
│   └── SettingsViewModel.cs
│
└── Infrastructure
    ├── TrayIconManager.cs
    ├── MonitorManager.cs
    └── FullscreenDetector.cs
```

---

## 18. Monitor Manager

`MonitorManager` chịu trách nhiệm:

```text
Detect monitors
Detect primary monitor
Detect DPI
Detect working area
Detect taskbar
Track monitor connection changes
Create StatusBarWindow per monitor
Restore saved monitor configuration
```

Mỗi màn hình được định danh bằng thông tin ổn định nhất có thể, tránh chỉ dựa vào:

```text
Monitor 1
Monitor 2
```

vì Windows có thể thay đổi thứ tự màn hình sau khi reconnect.

---

## 19. Luồng hoạt động

```text
App Start
   │
   ▼
Load Settings
   │
   ▼
Detect Monitors
   │
   ▼
Resolve Selected Monitors
   │
   ▼
Create Status Bar
for each monitor
   │
   ▼
Initialize Tray
   │
   ▼
Detect Codex
   │
   ▼
Read Usage
   │
   ▼
Update All Status Bars
   │
   ▼
Check Warning Threshold
   │
   ▼
Wait Refresh Interval
   │
   └───────────────► Repeat
```

---

## 20. UI đề xuất

### Compact Mode

```text
CODEX | S 72% | C 38% | Q 61%
```

### Full Mode

```text
CODEX | Session 72% | Context 38% | 5H 61% | Week 43%
```

### Minimal Mode

```text
C 38% | Q 61%
```

Người dùng có thể chọn mode trong Settings.

---

## 21. Multi-Monitor Example

Ví dụ setup:

```text
Laptop
Monitor 1 - 1920x1080

External monitor
Monitor 2 - 2560x1440
```

### Chỉ hiện trên Monitor 2

```text
Show on:

☐ Monitor 1
☑ Monitor 2
```

Kết quả:

```text
Monitor 1
[ Không hiển thị ]

Monitor 2
                         ┌────────────────────────────┐
                         │ C 38% | 5H 61% | W 43%    │
                         └────────────────────────────┘
```

### Hiện trên cả 2 màn hình

```text
Show on:

☑ Monitor 1
☑ Monitor 2
```

Kết quả:

```text
Monitor 1
CODEX | C 38% | Q 61%

Monitor 2
CODEX | C 38% | Q 61%
```

---

## 22. Ưu tiên phát triển

### Phase 1

- System Tray
- Floating Status Bar
- Session usage
- Context usage
- Quota usage
- Auto refresh
- Multi-monitor detection
- Chọn màn hình hiển thị
- Hiển thị trên một hoặc nhiều màn hình
- Basic settings

### Phase 2

- Windows notification
- Warning threshold
- Start with Windows
- Per-monitor position
- DPI scaling
- Monitor hot plug
- Fullscreen detection

### Phase 3

- Multi-session monitoring
- Session history
- Usage chart
- Daily/weekly statistics
- Export usage data

---

## 23. Nguyên tắc bảo mật

Ứng dụng không:

- Upload credential
- Log access token
- Log password
- Gửi usage ra server riêng
- Inject code vào Codex
- Can thiệp vào Codex process

Ứng dụng chỉ đọc dữ liệu cần thiết để hiển thị usage/status.

---

## 24. Kết quả mong muốn

Sau khi cài đặt, người dùng có thể làm việc bình thường trong Visual Studio, VS Code hoặc Codex mà vẫn luôn nhìn thấy:

```text
Session
Context
Quota
Reset time
```

ngay tại khu vực Taskbar.

Với máy có nhiều màn hình, người dùng có thể chọn:

```text
Monitor 1 only
Monitor 2 only
Specific monitors
All monitors
```

và ứng dụng sẽ tự ghi nhớ cấu hình cho những lần chạy tiếp theo.
