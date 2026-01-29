using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PCTFFM.Tools {
    internal class Tol {

        /// <summary>
        /// C:\Users\user\AppData\Roaming\[app_name]을 변환합니다. 만약 디랙토리에 해당하는 폴더가 없을 경우 새로 만듭니다.
        /// </summary>
        public static string AppdataPath {
            get {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Application.ProductName);
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return path;
            }
        }

        public static void ShowInfo(string text) {
            MessageBox.Show(text, "정보",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        public static void ShowError(string text) {
            MessageBox.Show(text, "오류",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        public static void ShowWarning(string text) {
            MessageBox.Show(text, "경고",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        public static bool ShowQ(string text) {
            return MessageBox.Show(text, "질문",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) == DialogResult.Yes;
        }

        private const string DefaultDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        public class FFmpegProgress {
            public int Percentage { get; set; }
            public string Message { get; set; }
        }


        public static async Task EnsureFFmpegAsync(
            string targetDirectory,
            IProgress<FFmpegProgress> progress = null,
            string downloadUrl = DefaultDownloadUrl) {
            string ffmpegPath = Path.Combine(targetDirectory, "ffmpeg.exe");

            if (File.Exists(ffmpegPath))
                return;
            else {
                if (!Tol.ShowQ("FFmpeg 구성 요소가 설치되어 있지 않습니다.\n" +
                    "해당 기능을 사용하려면 FFmpeg 다운로드가 필요합니다.\n\n" +
                    "지금 다운로드하시겠습니까?")) {
                    Application.Exit();
                    return;
                }
            }

            string zipPath = Path.Combine(Path.GetTempPath(), "ffmpeg.zip");
            string extractPath = Path.Combine(Path.GetTempPath(), "ffmpeg_extract");

            if (Directory.Exists(extractPath))
                Directory.Delete(extractPath, true);

            using (var wc = new WebClient()) {
                wc.DownloadProgressChanged += (s, e) => {
                    progress?.Report(new FFmpegProgress {
                        Percentage = e.ProgressPercentage,
                        Message = $"FFmpeg 다운로드 중... {e.ProgressPercentage}%"
                    });
                };

                await wc.DownloadFileTaskAsync(new Uri(downloadUrl), zipPath);
            }

            progress?.Report(new FFmpegProgress {
                Percentage = 0,
                Message = "압축 해제 중..."
            });

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

            progress?.Report(new FFmpegProgress {
                Percentage = 100,
                Message = "FFmpeg 준비 완료"
            });
        }

        public static string FormatFileSize(long bytes) {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public static string FormatTimeSpan(TimeSpan time) {
            if (time.TotalMinutes < 1)
                return $"{time.Seconds}초";
            return $"{time.Minutes}분 {time.Seconds}초";
        }

        public static string GetQualityArguments(string format) {
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
    }
}
