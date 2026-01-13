using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PCTFFM {
    public partial class MainForm : Form {
        private List<FileItem> fileList = new List<FileItem>();
        private string outputFolder = string.Empty;
        private CancellationTokenSource cancellationTokenSource;
        private int maxParallelTasks = 2; // 동시 변환 파일 수
        private Stopwatch conversionStopwatch = new Stopwatch();

        public MainForm() {
            InitializeComponent();
            InitializeListView();
            LoadSettings();

            Shown += async (s, e) => {
                try {
                    ToggleControls(false);
                    labelProgress.Text = "(최초 실행시 시도) FFmpeg 준비 중...";
                    progressBar.Value = 0;

                    await EnsureFFmpegAsyncWithProgress();

                    labelProgress.Text = "준비 완료!";
                    progressBar.Value = 0;
                } catch (Exception ex) {
                    MessageBox.Show(
                        "FFmpeg 초기화 실패:\n" + ex.Message,
                        "오류",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    Close();
                } finally {
                    progressBar.Style = ProgressBarStyle.Blocks;
                    ToggleControls(true);
                }
            };
        }


        // ListView 초기화 - 컬럼 추가
        private void InitializeListView() {
            listViewFiles.View = View.Details;
            listViewFiles.Columns.Clear();
            listViewFiles.Columns.Add("파일명", 250);
            listViewFiles.Columns.Add("크기", 80);
            listViewFiles.Columns.Add("상태", 100);
            listViewFiles.FullRowSelect = true;
        }

        // 설정 로드 (선택사항)
        private void LoadSettings() {
            // 마지막 출력 폴더 등 저장된 설정 로드 가능
        }

        // 파일 타입 선택 시 변환 형식 초기화
        private void comboBoxFileType_SelectedIndexChanged(object sender, EventArgs e) {
            if (fileList.Count > 0) {
                var result = MessageBox.Show("파일 타입을 변경하면 목록이 초기화됩니다. 계속하시겠습니까?",
                    "확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.No) return;

                fileList.Clear();
                listViewFiles.Items.Clear();
            }

            comboBoxFormat.Items.Clear();

            if (comboBoxFileType.SelectedItem.ToString() == "비디오") {
                comboBoxFormat.Items.AddRange(new object[] { "mp4", "avi", "mkv", "mov", "wmv", "gif", "mp3" });
            } else if (comboBoxFileType.SelectedItem.ToString() == "이미지") {
                comboBoxFormat.Items.AddRange(new object[] { "jpg", "png", "gif", "bmp", "tiff", "ico", "heic", "svg", "webp" });
            } else if (comboBoxFileType.SelectedItem.ToString() == "오디오") {
                comboBoxFormat.Items.AddRange(new object[] { "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma" });
            }
        }

        // 파일 추가 버튼 클릭
        private void btnAddFiles_Click(object sender, EventArgs e) {
            AddFile();
        }

        private void AddFile() {
            using (OpenFileDialog openFileDialog = new OpenFileDialog()) {
                openFileDialog.Multiselect = true;
                openFileDialog.Filter = "모든 파일(*.*)|*.*";

                if (openFileDialog.ShowDialog() == DialogResult.OK) {
                    int addedCount = 0;
                    int duplicateCount = 0;

                    foreach (var file in openFileDialog.FileNames) {
                        // 중복 체크
                        if (fileList.Any(f => f.FilePath == file)) {
                            duplicateCount++;
                            continue;
                        }

                        var fileItem = new FileItem {
                            FilePath = file,
                            FileSize = new FileInfo(file).Length,
                            Status = "대기 중"
                        };

                        fileList.Add(fileItem);
                        AddListViewItem(fileItem);
                        addedCount++;
                    }

                    if (duplicateCount > 0) {
                        MessageBox.Show($"{addedCount}개 파일 추가됨. {duplicateCount}개 중복 파일 제외됨.",
                            "정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }

        // ListView에 아이템 추가
        private void AddListViewItem(FileItem fileItem) {
            var item = new ListViewItem(Path.GetFileName(fileItem.FilePath));
            item.SubItems.Add(FormatFileSize(fileItem.FileSize));
            item.SubItems.Add(fileItem.Status);
            item.Tag = fileItem;
            listViewFiles.Items.Add(item);
        }

        // 파일 크기 포맷
        private string FormatFileSize(long bytes) {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        // 변환 시작 버튼 클릭
        private async void btnStartConversion_Click(object sender, EventArgs e) {
            btnStartConversion.Enabled = false;

            try {
                Start();
            } catch (Exception ex) {
                MessageBox.Show("오류 발생:\n" + ex.Message);
            } finally {
                btnStartConversion.Enabled = true;
            }
        }

        private async Task EnsureFFmpegAsyncWithProgress() {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegPath = Path.Combine(baseDir, "ffmpeg.exe");

            if (File.Exists(ffmpegPath))
                return;

            string zipPath = Path.Combine(Path.GetTempPath(), "ffmpeg.zip");
            string extractPath = Path.Combine(Path.GetTempPath(), "ffmpeg_extract");

            if (Directory.Exists(extractPath))
                Directory.Delete(extractPath, true);

            using (var wc = new WebClient()) {

                wc.DownloadProgressChanged += (s, e) => {
                    Invoke(new Action(() => {
                        progressBar.Value = e.ProgressPercentage;
                        labelProgress.Text = $"(최초 실행시 시도) FFmpeg 다운로드 중... {e.ProgressPercentage}%";
                    }));
                };

                await wc.DownloadFileTaskAsync(
                    new Uri("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"),
                    zipPath
                );
            }

            Invoke(new Action(() => {
                progressBar.Value = 0;
                labelProgress.Text = "압축 해제 중...";
            }));

            await Task.Run(() => {
                ZipFile.ExtractToDirectory(zipPath, extractPath);
            });

            string extractedFFmpeg = Directory
                .GetFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (extractedFFmpeg == null)
                throw new FileNotFoundException("ffmpeg.exe를 찾을 수 없습니다.");

            File.Copy(extractedFFmpeg, ffmpegPath, overwrite: true);

            File.Delete(zipPath);
            Directory.Delete(extractPath, true);
        }

        private async void Start() {
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

            if (!File.Exists(ffmpegPath)) {
                MessageBox.Show("ffmpeg.exe가 없습니다.");
                return;
            }

            if (fileList.Count == 0 || comboBoxFormat.SelectedItem == null) {
                MessageBox.Show("파일과 변환할 형식을 선택해주세요.", "정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrEmpty(outputFolder)) {
                MessageBox.Show("출력 폴더를 지정해주세요.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            cancellationTokenSource = new CancellationTokenSource();
            string outputFormat = comboBoxFormat.SelectedItem.ToString();

            ToggleControls(false);
            progressBar.Maximum = fileList.Count;
            progressBar.Value = 0;
            labelProgress.Text = "변환 중... 0%";
            conversionStopwatch.Restart();

            int completed = 0;
            int failed = 0;
            var semaphore = new SemaphoreSlim(maxParallelTasks);

            var tasks = fileList.Select(async (fileItem, index) => {
                await semaphore.WaitAsync(cancellationTokenSource.Token);
                try {
                    if (cancellationTokenSource.Token.IsCancellationRequested) return;

                    string outputFilePath = Path.Combine(outputFolder,
                        Path.GetFileNameWithoutExtension(fileItem.FilePath) + "." + outputFormat);

                    UpdateFileStatus(fileItem, "변환 중...", Color.LightYellow);

                    bool success = await Task.Run(() =>
                        ConvertFileWithFFmpeg(fileItem.FilePath, outputFilePath, outputFormat,
                            cancellationTokenSource.Token));

                    if (success) {
                        UpdateFileStatus(fileItem, "완료", Color.LightGreen);
                        Interlocked.Increment(ref completed);
                    } else {
                        UpdateFileStatus(fileItem, "실패", Color.LightCoral);
                        Interlocked.Increment(ref failed);
                    }

                    // UI 업데이트
                    Invoke(new Action(() => {
                        progressBar.Value++;
                        int percentage = (int)((double)progressBar.Value / progressBar.Maximum * 100);
                        labelProgress.Text = $"변환 중... {percentage}% ({progressBar.Value}/{progressBar.Maximum})";
                    }));

                } catch (Exception ex) {
                    UpdateFileStatus(fileItem, "오류", Color.LightCoral);
                    Interlocked.Increment(ref failed);
                    Debug.WriteLine($"변환 오류: {ex.Message}");
                } finally {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            conversionStopwatch.Stop();

            // 원본 파일 삭제
            if (checkBox1.Checked) {
                foreach (var fileItem in fileList.Where(f => f.Status == "완료")) {
                    try {
                        File.Delete(fileItem.FilePath);
                    } catch (Exception e) {
                        MessageBox.Show($"원본 파일 삭제 도중 오류가 발생했습니다.\n경로: {fileItem.FilePath}\n\n{e.Message}",
                            "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }

            labelProgress.Text = $"완료: {completed}개, 실패: {failed}개 (소요시간: {FormatTimeSpan(conversionStopwatch.Elapsed)})";
            ToggleControls(true);

            if (failed > 0) {
                MessageBox.Show($"일부 파일 변환에 실패했습니다.\n성공: {completed}개\n실패: {failed}개",
                    "변환 완료", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 시간 포맷
        private string FormatTimeSpan(TimeSpan time) {
            if (time.TotalMinutes < 1)
                return $"{time.Seconds}초";
            return $"{time.Minutes}분 {time.Seconds}초";
        }

        // 파일 상태 업데이트
        private void UpdateFileStatus(FileItem fileItem, string status, Color color) {
            fileItem.Status = status;

            if (InvokeRequired) {
                Invoke(new Action(() => UpdateFileStatus(fileItem, status, color)));
                return;
            }

            foreach (ListViewItem item in listViewFiles.Items) {
                if (item.Tag == fileItem) {
                    item.SubItems[2].Text = status;
                    item.BackColor = color;
                    break;
                }
            }
        }

        // 컨트롤 활성화/비활성화
        private void ToggleControls(bool isEnabled) {
            btnAddFiles.Enabled = isEnabled;
            btnStartConversion.Enabled = isEnabled;
            comboBoxFileType.Enabled = isEnabled;
            comboBoxFormat.Enabled = isEnabled;
            btnSelectOutputFolder.Enabled = isEnabled;
            btnDelete.Enabled = isEnabled;
            numericUpDownThreads.Enabled = isEnabled;
            btnViewFile.Enabled = isEnabled;
            comboBoxFileType.Enabled = isEnabled;
            comboBoxFormat.Enabled = isEnabled;
            checkBox1.Enabled = isEnabled;
            btnClearAll.Enabled = isEnabled;
            btnCancel.Enabled = !isEnabled;
        }

        // 변환 취소 버튼 클릭
        private void btnCancel_Click(object sender, EventArgs e) {
            if (cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested) {
                var result = MessageBox.Show("변환을 취소하시겠습니까?", "확인",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.Yes) {
                    cancellationTokenSource?.Cancel();
                    labelProgress.Text = "변환 취소됨";
                }
            }
        }

        // 출력 폴더 선택 버튼 클릭
        private void btnSelectOutputFolder_Click(object sender, EventArgs e) {
            using (CommonOpenFileDialog dialog = new CommonOpenFileDialog()) {
                dialog.Multiselect = false;
                dialog.IsFolderPicker = true;
                if (dialog.ShowDialog() == CommonFileDialogResult.Ok) {
                    outputFolder = dialog.FileName;
                    textBox1.Text = "출력 폴더: " + outputFolder;
                }
            }
        }

        // 삭제 버튼 클릭
        private void btnDelete_Click(object sender, EventArgs e) {
            DeleteRow();
        }

        private void DeleteRow() {
            if (listViewFiles.SelectedItems.Count > 0) {
                var itemsToRemove = new List<ListViewItem>();
                foreach (ListViewItem item in listViewFiles.SelectedItems) {
                    var fileItem = item.Tag as FileItem;
                    if (fileItem != null) {
                        fileList.Remove(fileItem);
                    }
                    itemsToRemove.Add(item);
                }

                foreach (var item in itemsToRemove) {
                    listViewFiles.Items.Remove(item);
                }
            } else {
                MessageBox.Show("삭제할 파일을 선택하세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // 파일 보기 버튼 클릭
        private void btnViewFile_Click(object sender, EventArgs e) {
            FileView();
        }

        private void FileView() {
            if (listViewFiles.SelectedItems.Count > 0) {
                var fileItem = listViewFiles.SelectedItems[0].Tag as FileItem;
                if (fileItem != null && File.Exists(fileItem.FilePath)) {
                    Process.Start("explorer.exe", $"/select, \"{fileItem.FilePath}\"");
                } else {
                    MessageBox.Show("파일이 존재하지 않습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            } else {
                MessageBox.Show("보기할 파일을 선택하세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // FFmpeg를 이용한 파일 변환
        private bool ConvertFileWithFFmpeg(string inputFile, string outputFile, string format, CancellationToken cancellationToken) {
            try {
                // 품질 설정에 따른 FFmpeg 인자
                string qualityArgs = GetQualityArguments(format);
                string ffmpegArguments = $"-i \"{inputFile}\" {qualityArgs} -y \"{outputFile}\"";

                ProcessStartInfo processStartInfo = new ProcessStartInfo {
                    FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                    Arguments = ffmpegArguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(processStartInfo)) {
                    // 에러 출력만 읽기 (FFmpeg는 진행상황을 stderr로 출력)
                    var errorBuilder = new System.Text.StringBuilder();
                    process.ErrorDataReceived += (s, e) => {
                        if (!string.IsNullOrEmpty(e.Data)) {
                            errorBuilder.AppendLine(e.Data);
                        }
                    };

                    process.BeginErrorReadLine();

                    while (!process.WaitForExit(100)) {
                        if (cancellationToken.IsCancellationRequested) {
                            process.Kill();
                            return false;
                        }
                    }

                    return process.ExitCode == 0;
                }
            } catch (Exception ex) {
                Debug.WriteLine($"FFmpeg 오류: {ex.Message}");
                return false;
            }
        }

        // 품질 설정 가져오기
        private string GetQualityArguments(string format) {
            // 비디오 포맷
            if (new[] { "mp4", "avi", "mkv", "mov", "wmv" }.Contains(format)) {
                return "-c:v libx264 -crf 23 -c:a aac -b:a 128k";
            }
            // 오디오 포맷
            else if (new[] { "mp3", "aac", "ogg" }.Contains(format)) {
                return "-b:a 192k";
            }
            // 이미지는 기본 설정
            return "";
        }

        // 드래그 앤 드롭
        private void listViewFiles_DragDrop(object sender, DragEventArgs e) {
            button1.BackColor = Color.White;

            if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
                string[] droppedItems = (string[])e.Data.GetData(DataFormats.FileDrop);
                int addedCount = 0;

                foreach (var item in droppedItems) {
                    if (!Directory.Exists(item) && !fileList.Any(f => f.FilePath == item)) {
                        var fileItem = new FileItem {
                            FilePath = item,
                            FileSize = new FileInfo(item).Length,
                            Status = "대기 중"
                        };
                        fileList.Add(fileItem);
                        AddListViewItem(fileItem);
                        addedCount++;
                    }
                }

                if (addedCount > 0) {
                    labelProgress.Text = $"{addedCount}개 파일 추가됨";
                }
            }
        }

        private void listViewFiles_DragEnter(object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
                string[] droppedItems = (string[])e.Data.GetData(DataFormats.FileDrop);

                if (droppedItems.All(item => !Directory.Exists(item))) {
                    e.Effect = DragDropEffects.Copy;
                    button1.BackColor = Color.LightGreen;
                } else {
                    e.Effect = DragDropEffects.None;
                    button1.BackColor = Color.LightCoral;
                }
            } else {
                e.Effect = DragDropEffects.None;
            }
        }

        private void listViewFiles_DragLeave(object sender, EventArgs e) {
            button1.BackColor = Color.White;
        }

        // 키보드 단축키
        private void listViewFiles_KeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Delete) {
                btnDelete.PerformClick();
            }
            if (e.Control && e.KeyCode == Keys.A) {
                foreach (ListViewItem item in listViewFiles.Items) {
                    item.Selected = true;
                }
            }
            if (e.Control && e.KeyCode == Keys.F) {
                btnViewFile.PerformClick();
            }
            if (e.Control && e.Shift && e.KeyCode == Keys.A) {
                btnAddFiles.PerformClick();
            }
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.F10) {
                Start();
            }
        }

        // 동시 변환 수 변경
        private void numericUpDownThreads_ValueChanged(object sender, EventArgs e) {
            maxParallelTasks = (int)numericUpDownThreads.Value;
        }

        // 모두 삭제 버튼
        private void btnClearAll_Click(object sender, EventArgs e) {
            if (fileList.Count > 0) {
                var result = MessageBox.Show("모든 파일을 목록에서 제거하시겠습니까?", "확인",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.Yes) {
                    fileList.Clear();
                    listViewFiles.Items.Clear();
                    labelProgress.Text = "목록이 비워졌습니다.";
                }
            }
        }
    }

    // 파일 정보 클래스
    public class FileItem {
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public string Status { get; set; }
    }
}