# -*- coding: utf-8 -*-
"""JPG/PNG/WEBP 一括リサイズ・フォーマット変換ツール

フォルダ内の JPG/PNG/WEBP を長辺指定でリサイズし、別フォルダへ出力する。
出力フォーマット（JPG / PNG / WEBP）の変換、ファイル名の小文字化・置換・
末尾文字列付与を変換と同時に行える。
"""

import threading
import queue
from dataclasses import dataclass
from pathlib import Path

import tkinter as tk
from tkinter import ttk, filedialog, messagebox, scrolledtext

from PIL import Image, ImageOps

APP_TITLE = "画像リサイズ・フォーマット変換（JPG / PNG / WEBP）"

SIZE_PRESETS = [1600, 1200, 600, 560]

# 「任意」ラジオボタンを表す値（プリセットと重複しない番兵値）
CUSTOM_SIZE = 0

# 「サイズを変更しない」を表す値（フォーマット変換だけを行いたい場合に使う）
KEEP_SIZE = -1

# 長辺として指定できる最大値（JPEG 形式の上限に合わせる）
MAX_LONG_EDGE = 65500

RESAMPLE_METHODS = {
    "Bilinear": Image.BILINEAR,
    "Bicubic": Image.BICUBIC,
    "Lanczos": Image.LANCZOS,
}

TARGET_EXTS = {".jpg", ".jpeg", ".png", ".webp"}

# 出力フォーマット（KEEP は元ファイルの拡張子をそのまま使う）
FORMAT_KEEP = "keep"
FORMAT_JPEG = "jpeg"
FORMAT_PNG = "png"
FORMAT_WEBP = "webp"

FORMAT_LABELS = [("元のまま", FORMAT_KEEP), ("JPG", FORMAT_JPEG),
                 ("PNG", FORMAT_PNG), ("WEBP", FORMAT_WEBP)]
FORMAT_EXTS = {FORMAT_JPEG: ".jpg", FORMAT_PNG: ".png", FORMAT_WEBP: ".webp"}

JPEG_QUALITY = 90
WEBP_QUALITY = 90


@dataclass
class ConvertOptions:
    long_edge: int
    resample: int
    lowercase: bool          # ファイル名と拡張子を小文字化
    replace_search: str      # 置換元文字列（空なら置換しない）
    replace_with: str        # 置換先文字列
    suffix: str              # ファイル名末尾に付与する文字列
    overwrite: bool          # True=上書き / False=スキップ
    no_upscale: bool         # 長辺が指定値より小さい画像は拡大しない
    strip_metadata: bool = True  # EXIF・コメント等のメタデータを削除する
    out_format: str = FORMAT_KEEP  # 出力フォーマット（FORMAT_* のいずれか）


def build_dest_name(src: Path, opts: ConvertOptions) -> str:
    """変換オプションに従って出力ファイル名を組み立てる。"""
    stem = src.stem
    if opts.replace_search:
        stem = stem.replace(opts.replace_search, opts.replace_with)
    stem += opts.suffix
    ext = FORMAT_EXTS.get(opts.out_format, src.suffix)
    if opts.lowercase:
        stem = stem.lower()
        ext = ext.lower()
    return stem + ext


def describe_options(opts: ConvertOptions, algo_name: str) -> str:
    """ログ見出し用に、サイズ・形式・アルゴリズムを1行にまとめる。"""
    size = "サイズ変更なし" if opts.long_edge == KEEP_SIZE else f"長辺{opts.long_edge}px"
    fmt = next(label for label, value in FORMAT_LABELS if value == opts.out_format)
    return f"{size} / 形式{fmt} / {algo_name}"


def resize_image(img: Image.Image, opts: ConvertOptions) -> Image.Image:
    """長辺を指定サイズに合わせてリサイズする。"""
    if opts.long_edge == KEEP_SIZE:
        return img
    w, h = img.size
    long_now = max(w, h)
    if long_now == opts.long_edge:
        return img
    if opts.no_upscale and long_now < opts.long_edge:
        return img
    scale = opts.long_edge / long_now
    new_size = (max(1, round(w * scale)), max(1, round(h * scale)))
    return img.resize(new_size, opts.resample)


def flatten_for_jpeg(img: Image.Image) -> Image.Image:
    """JPEG は透過を扱えないため、透過部分を白で塗りつぶして不透明化する。"""
    if img.mode in ("RGB", "L"):
        return img
    if img.mode in ("RGBA", "LA") or (img.mode == "P" and "transparency" in img.info):
        rgba = img.convert("RGBA")
        bg = Image.new("RGB", rgba.size, (255, 255, 255))
        bg.paste(rgba, mask=rgba.split()[3])
        return bg
    return img.convert("RGB")


def convert_one(src: Path, dst: Path, opts: ConvertOptions) -> str:
    """1ファイルを変換する。戻り値は 'converted' / 'skipped'。"""
    if dst.exists() and not opts.overwrite:
        return "skipped"

    with Image.open(src) as img:
        # EXIF の回転情報を反映してから処理する
        img = ImageOps.exif_transpose(img)
        img.load()
        out = resize_image(img, opts)

        # 出力拡張子で保存形式が決まる（build_dest_name が out_format を反映済み）
        ext = dst.suffix.lower()
        to_jpeg = ext in (".jpg", ".jpeg")
        if to_jpeg:
            # 透過の判定に info を使うため、メタデータを落とす前に行う
            out = flatten_for_jpeg(out)

        save_kwargs = {}
        icc = img.info.get("icc_profile")
        if icc:
            save_kwargs["icc_profile"] = icc

        if opts.strip_metadata:
            # Pillow は保存時に info の exif / comment / xmp を暗黙に
            # 書き込むことがあるため、info ごと空にして確実に落とす
            out.info = {}
        elif img.info.get("exif"):
            save_kwargs["exif"] = img.info["exif"]

        if to_jpeg:
            out.save(dst, format="JPEG", quality=JPEG_QUALITY,
                     optimize=True, **save_kwargs)
        elif ext == ".webp":
            out.save(dst, format="WEBP", quality=WEBP_QUALITY, **save_kwargs)
        else:
            out.save(dst, format="PNG", optimize=True, **save_kwargs)

    return "converted"


def collect_targets(folder: Path) -> list[Path]:
    """フォルダ直下の JPG/PNG/WEBP を列挙する。"""
    return sorted(
        p for p in folder.iterdir()
        if p.is_file() and p.suffix.lower() in TARGET_EXTS
    )


def process_folder(in_dir: Path, out_dir: Path, opts: ConvertOptions,
                   report=lambda *a: None, is_cancelled=lambda: False) -> dict:
    """フォルダ単位の変換。report(kind, *args) で進捗を通知する。"""
    files = collect_targets(in_dir)
    total = len(files)
    stats = {"total": total, "converted": 0, "skipped": 0, "error": 0}
    report("start", total)

    out_dir.mkdir(parents=True, exist_ok=True)

    for i, src in enumerate(files, 1):
        if is_cancelled():
            report("log", "―― 中断しました ――")
            break
        dst = out_dir / build_dest_name(src, opts)
        try:
            result = convert_one(src, dst, opts)
            stats[result] += 1
            if result == "skipped":
                report("log", f"スキップ: {src.name} → {dst.name}（同名ファイルあり）")
            else:
                report("log", f"変換: {src.name} → {dst.name}")
        except Exception as e:
            stats["error"] += 1
            report("log", f"エラー: {src.name} … {e}")
        report("progress", i, total)

    report("done", stats)
    return stats


# ---------------------------------------------------------------- GUI

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(APP_TITLE)
        self.resizable(True, False)

        self.msg_queue: queue.Queue = queue.Queue()
        self.worker: threading.Thread | None = None
        self.cancel_flag = threading.Event()

        self._build_ui()
        self._poll_queue()

    # ---- UI 構築 ----
    def _build_ui(self):
        pad = {"padx": 8, "pady": 4}
        root = ttk.Frame(self)
        root.pack(fill="both", expand=True, padx=10, pady=10)

        # フォルダ選択
        io = ttk.LabelFrame(root, text="フォルダ")
        io.pack(fill="x", **pad)
        io.columnconfigure(1, weight=1)

        self.in_dir = tk.StringVar()
        self.out_dir = tk.StringVar()
        ttk.Label(io, text="入力フォルダ:").grid(row=0, column=0, sticky="e", **pad)
        ttk.Entry(io, textvariable=self.in_dir).grid(row=0, column=1, sticky="ew", **pad)
        ttk.Button(io, text="参照...", command=self._browse_in).grid(row=0, column=2, **pad)
        ttk.Label(io, text="出力フォルダ:").grid(row=1, column=0, sticky="e", **pad)
        ttk.Entry(io, textvariable=self.out_dir).grid(row=1, column=1, sticky="ew", **pad)
        ttk.Button(io, text="参照...", command=self._browse_out).grid(row=1, column=2, **pad)

        # サイズとアルゴリズム
        conv = ttk.LabelFrame(root, text="変換設定")
        conv.pack(fill="x", **pad)

        size_row = ttk.Frame(conv)
        size_row.pack(fill="x", **pad)
        ttk.Label(size_row, text="サイズ:").pack(side="left")
        self.size_var = tk.IntVar(value=SIZE_PRESETS[0])
        for s in SIZE_PRESETS:
            ttk.Radiobutton(size_row, text=f"長辺 {s}px", value=s,
                            variable=self.size_var).pack(side="left", padx=6)
        ttk.Radiobutton(size_row, text="任意:", value=CUSTOM_SIZE,
                        variable=self.size_var).pack(side="left", padx=(6, 0))
        self.custom_size_var = tk.StringVar()
        custom_entry = ttk.Entry(size_row, textvariable=self.custom_size_var,
                                 width=7, justify="right")
        custom_entry.pack(side="left")
        ttk.Label(size_row, text="px").pack(side="left", padx=(2, 6))
        ttk.Radiobutton(size_row, text="変更しない", value=KEEP_SIZE,
                        variable=self.size_var).pack(side="left", padx=6)
        # 入力欄に触れたら自動で「任意」を選択する
        custom_entry.bind("<FocusIn>",
                          lambda _e: self.size_var.set(CUSTOM_SIZE))

        fmt_row = ttk.Frame(conv)
        fmt_row.pack(fill="x", **pad)
        ttk.Label(fmt_row, text="出力形式:").pack(side="left")
        self.format_var = tk.StringVar(value=FORMAT_KEEP)
        for label, value in FORMAT_LABELS:
            ttk.Radiobutton(fmt_row, text=label, value=value,
                            variable=self.format_var).pack(side="left", padx=6)
        ttk.Label(fmt_row, text="（JPGへの変換では透過部分を白で塗りつぶします）").pack(
            side="left", padx=(6, 0))

        algo_row = ttk.Frame(conv)
        algo_row.pack(fill="x", **pad)
        ttk.Label(algo_row, text="アルゴリズム:").pack(side="left")
        self.algo_var = tk.StringVar(value="Lanczos")
        for name in RESAMPLE_METHODS:
            ttk.Radiobutton(algo_row, text=name, value=name,
                            variable=self.algo_var).pack(side="left", padx=6)

        self.no_upscale_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(conv, text="長辺が指定サイズより小さい画像は拡大しない",
                        variable=self.no_upscale_var).pack(anchor="w", **pad)

        self.strip_meta_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            conv,
            text="EXIF・コメントなどのメタデータを削除する（ICCプロファイルは保持）",
            variable=self.strip_meta_var).pack(anchor="w", **pad)

        # ファイル名オプション
        name_f = ttk.LabelFrame(root, text="ファイル名オプション")
        name_f.pack(fill="x", **pad)
        name_f.columnconfigure(1, weight=1)
        name_f.columnconfigure(3, weight=1)

        self.lowercase_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(name_f, text="ファイル名と拡張子を小文字化する",
                        variable=self.lowercase_var).grid(
                            row=0, column=0, columnspan=4, sticky="w", **pad)

        self.replace_search = tk.StringVar()
        self.replace_with = tk.StringVar()
        ttk.Label(name_f, text="置換元:").grid(row=1, column=0, sticky="e", **pad)
        ttk.Entry(name_f, textvariable=self.replace_search).grid(row=1, column=1, sticky="ew", **pad)
        ttk.Label(name_f, text="置換先:").grid(row=1, column=2, sticky="e", **pad)
        ttk.Entry(name_f, textvariable=self.replace_with).grid(row=1, column=3, sticky="ew", **pad)

        self.suffix_var = tk.StringVar()
        ttk.Label(name_f, text="末尾に付与:").grid(row=2, column=0, sticky="e", **pad)
        ttk.Entry(name_f, textvariable=self.suffix_var).grid(row=2, column=1, sticky="ew", **pad)
        ttk.Label(name_f, text="（例: _s → photo_s.jpg）").grid(
            row=2, column=2, columnspan=2, sticky="w", **pad)

        # 同名ファイルの扱い
        dup = ttk.LabelFrame(root, text="出力先に同名ファイルがある場合")
        dup.pack(fill="x", **pad)
        self.overwrite_var = tk.BooleanVar(value=False)
        row = ttk.Frame(dup)
        row.pack(fill="x", **pad)
        ttk.Radiobutton(row, text="処理をスキップ", value=False,
                        variable=self.overwrite_var).pack(side="left", padx=6)
        ttk.Radiobutton(row, text="上書き", value=True,
                        variable=self.overwrite_var).pack(side="left", padx=6)

        # 実行・進捗
        run_row = ttk.Frame(root)
        run_row.pack(fill="x", **pad)
        self.run_btn = ttk.Button(run_row, text="変換開始", command=self._start)
        self.run_btn.pack(side="left", padx=4)
        self.cancel_btn = ttk.Button(run_row, text="中断", command=self._cancel,
                                     state="disabled")
        self.cancel_btn.pack(side="left", padx=4)
        self.progress = ttk.Progressbar(run_row, mode="determinate")
        self.progress.pack(side="left", fill="x", expand=True, padx=8)
        self.status_var = tk.StringVar(value="待機中")
        ttk.Label(run_row, textvariable=self.status_var, width=14,
                  anchor="e").pack(side="right")

        # ログ
        self.log = scrolledtext.ScrolledText(root, height=12, state="disabled")
        self.log.pack(fill="both", expand=True, **pad)

    # ---- イベント ----
    def _browse_in(self):
        d = filedialog.askdirectory(title="入力フォルダを選択")
        if d:
            self.in_dir.set(d)
            if not self.out_dir.get():
                self.out_dir.set(str(Path(d) / "resized"))

    def _browse_out(self):
        d = filedialog.askdirectory(title="出力フォルダを選択")
        if d:
            self.out_dir.set(d)

    def _log(self, text: str):
        self.log.configure(state="normal")
        self.log.insert("end", text + "\n")
        self.log.see("end")
        self.log.configure(state="disabled")

    def _start(self):
        in_dir = Path(self.in_dir.get().strip())
        out_dir_s = self.out_dir.get().strip()

        if not self.in_dir.get().strip() or not in_dir.is_dir():
            messagebox.showerror(APP_TITLE, "入力フォルダを正しく指定してください。")
            return
        if not out_dir_s:
            messagebox.showerror(APP_TITLE, "出力フォルダを指定してください。")
            return
        out_dir = Path(out_dir_s)

        long_edge = self.size_var.get()
        if long_edge == CUSTOM_SIZE:
            try:
                long_edge = int(self.custom_size_var.get().strip())
            except ValueError:
                long_edge = 0
            if not (1 <= long_edge <= MAX_LONG_EDGE):
                messagebox.showerror(
                    APP_TITLE,
                    f"任意サイズには 1〜{MAX_LONG_EDGE} の整数を入力してください。")
                return

        opts = ConvertOptions(
            long_edge=long_edge,
            resample=RESAMPLE_METHODS[self.algo_var.get()],
            lowercase=self.lowercase_var.get(),
            replace_search=self.replace_search.get(),
            replace_with=self.replace_with.get(),
            suffix=self.suffix_var.get(),
            overwrite=self.overwrite_var.get(),
            no_upscale=self.no_upscale_var.get(),
            strip_metadata=self.strip_meta_var.get(),
            out_format=self.format_var.get(),
        )

        if (in_dir.resolve() == out_dir.resolve() and opts.overwrite):
            if not messagebox.askyesno(
                    APP_TITLE,
                    "入力フォルダと出力フォルダが同じで「上書き」が選択されています。\n"
                    "元の画像ファイルが変換結果で置き換えられますが、続行しますか？"):
                return

        self.cancel_flag.clear()
        self.run_btn.configure(state="disabled")
        self.cancel_btn.configure(state="normal")
        self.status_var.set("処理中...")
        self._log(f"=== 変換開始: {describe_options(opts, self.algo_var.get())} ===")

        def report(kind, *args):
            self.msg_queue.put((kind, args))

        self.worker = threading.Thread(
            target=process_folder,
            args=(in_dir, out_dir, opts, report, self.cancel_flag.is_set),
            daemon=True,
        )
        self.worker.start()

    def _cancel(self):
        self.cancel_flag.set()
        self.cancel_btn.configure(state="disabled")

    # ---- ワーカーからの通知処理 ----
    def _poll_queue(self):
        try:
            while True:
                kind, args = self.msg_queue.get_nowait()
                if kind == "start":
                    total = args[0]
                    self.progress.configure(maximum=max(total, 1), value=0)
                    self._log(f"対象ファイル: {total}件")
                elif kind == "progress":
                    self.progress.configure(value=args[0])
                    self.status_var.set(f"{args[0]} / {args[1]}")
                elif kind == "log":
                    self._log(args[0])
                elif kind == "done":
                    s = args[0]
                    self._log(f"=== 完了: 変換 {s['converted']} / "
                              f"スキップ {s['skipped']} / エラー {s['error']} ===")
                    self.status_var.set("完了")
                    self.run_btn.configure(state="normal")
                    self.cancel_btn.configure(state="disabled")
        except queue.Empty:
            pass
        self.after(100, self._poll_queue)


if __name__ == "__main__":
    App().mainloop()
