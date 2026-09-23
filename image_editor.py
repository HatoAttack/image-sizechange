# -*- coding: utf-8 -*-
"""画像ツール（リサイズ・切り抜き・連結）

- リサイズ: フォルダ内の JPG/PNG/WEBP を長辺指定で一括リサイズし、出力形式を
  JPG / PNG / WEBP へ変換する（従来ツール image_resizer.py の処理コアをタブとして統合）。
- 切り抜き: 画像をマウスで範囲選択し、自由 / 固定 / 任意のアスペクト比で
  クロップして保存する。
- 連結: 複数の画像を横・縦・グリッドに1枚へ連結する（間隔・余白・背景色・
  サイズ揃え・整列を指定可能）。
"""

import math
import queue
import threading
from pathlib import Path

import tkinter as tk
from tkinter import ttk, filedialog, messagebox, colorchooser, scrolledtext

from PIL import Image, ImageOps, ImageTk

import image_resizer as R

# ドラッグ＆ドロップ（任意依存: pip install tkinterdnd2。無ければD&Dなしで動作）
try:
    from tkinterdnd2 import DND_FILES, TkinterDnD
except ImportError:
    DND_FILES = TkinterDnD = None

APP_TITLE = "画像ツール（リサイズ・切り抜き・連結）"

TARGET_EXTS = {".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"}
JPEG_QUALITY = 90

# 切り抜きのアスペクト比プリセット（表示名 -> 幅/高さ。None は自由比）
# 縦向きは「縦横を反転」チェックで対応する（4:3 → 3:4 など）
ASPECT_PRESETS = [
    ("自由", None),
    ("1:1", 1 / 1),
    ("4:3", 4 / 3),
    ("3:2", 3 / 2),
    ("16:9", 16 / 9),
]
CUSTOM_ASPECT = "任意"


# ================================================================ 画像処理コア

def load_image(path: Path) -> Image.Image:
    """EXIF 回転を反映して画像を読み込む。"""
    img = Image.open(path)
    img = ImageOps.exif_transpose(img)
    img.load()
    return img


def parse_color(hex_str: str, transparent: bool) -> tuple[int, int, int, int]:
    """'#RRGGBB' を RGBA タプルへ。透過指定時は完全透明を返す。"""
    if transparent:
        return (0, 0, 0, 0)
    s = hex_str.strip().lstrip("#")
    if len(s) == 3:
        s = "".join(c * 2 for c in s)
    if len(s) != 6:
        raise ValueError(f"色の指定が不正です: {hex_str}")
    r, g, b = (int(s[i:i + 2], 16) for i in (0, 2, 4))
    return (r, g, b, 255)


def _paste(canvas: Image.Image, im: Image.Image, pos: tuple[int, int]) -> None:
    """アルファを考慮して canvas に貼り付ける。"""
    if im.mode in ("RGBA", "LA") or (im.mode == "P" and "transparency" in im.info):
        im = im.convert("RGBA")
        canvas.paste(im, pos, im)
    else:
        canvas.paste(im.convert("RGB"), pos)


def _resize_to(img: Image.Image, target: int, dim: str) -> Image.Image:
    """dim='h' なら高さ、'w' なら幅を target に合わせ、他方を比例させる。"""
    w, h = img.size
    if dim == "h":
        if h == target:
            return img
        return img.resize((max(1, round(w * target / h)), target), Image.LANCZOS)
    if w == target:
        return img
    return img.resize((target, max(1, round(h * target / w))), Image.LANCZOS)


def _contain(img: Image.Image, cw: int, ch: int) -> Image.Image:
    """アスペクト比を保ったまま cw×ch の枠に収まるよう縮小する。"""
    w, h = img.size
    s = min(cw / w, ch / h)
    if s >= 1:
        return img
    return img.resize((max(1, round(w * s)), max(1, round(h * s))), Image.LANCZOS)


def _align_offset(total: int, size: int, align: str) -> int:
    if align == "center":
        return (total - size) // 2
    if align == "end":
        return total - size
    return 0


def combine_linear(imgs, horizontal: bool, normalize: str, target_px: int,
                   align: str, spacing: int, padding: int,
                   bg: tuple[int, int, int, int]) -> Image.Image:
    """横（horizontal=True）または縦に画像を連結する。

    normalize: 'none' / 'min' / 'max' / 'fixed'
        横連結では高さ、縦連結では幅を揃える基準。
    align: 'start' / 'center' / 'end'
    """
    perp = "h" if horizontal else "w"
    if normalize != "none":
        dims = [im.size[1] if horizontal else im.size[0] for im in imgs]
        target = {"min": min(dims), "max": max(dims)}.get(normalize, target_px)
        target = max(1, target)
        imgs = [_resize_to(im, target, perp) for im in imgs]

    n = len(imgs)
    if horizontal:
        content_h = max(im.size[1] for im in imgs)
        content_w = sum(im.size[0] for im in imgs) + spacing * (n - 1)
    else:
        content_w = max(im.size[0] for im in imgs)
        content_h = sum(im.size[1] for im in imgs) + spacing * (n - 1)

    canvas = Image.new("RGBA", (content_w + 2 * padding, content_h + 2 * padding), bg)
    cur = padding
    for im in imgs:
        if horizontal:
            y = padding + _align_offset(content_h, im.size[1], align)
            _paste(canvas, im, (cur, y))
            cur += im.size[0] + spacing
        else:
            x = padding + _align_offset(content_w, im.size[0], align)
            _paste(canvas, im, (x, cur))
            cur += im.size[1] + spacing
    return canvas


def combine_grid(imgs, columns: int, normalize: str, cell_px: int,
                 spacing: int, padding: int,
                 bg: tuple[int, int, int, int]) -> Image.Image:
    """画像をグリッド状（columns 列）に連結する。各画像はセルに収める。

    normalize: 'none'（各画像の最大寸法をセルに）/ 'fixed'（cell_px 角のセル）
    """
    if normalize == "fixed":
        cw = ch = max(1, cell_px)
    else:
        cw = max(im.size[0] for im in imgs)
        ch = max(im.size[1] for im in imgs)

    rows = math.ceil(len(imgs) / columns)
    W = columns * cw + spacing * (columns - 1) + 2 * padding
    H = rows * ch + spacing * (rows - 1) + 2 * padding
    canvas = Image.new("RGBA", (W, H), bg)

    for i, im in enumerate(imgs):
        r, c = divmod(i, columns)
        fit = _contain(im, cw, ch)
        cx = padding + c * (cw + spacing)
        cy = padding + r * (ch + spacing)
        ox = cx + (cw - fit.size[0]) // 2
        oy = cy + (ch - fit.size[1]) // 2
        _paste(canvas, fit, (ox, oy))
    return canvas


def save_image(img: Image.Image, dst: Path, quality: int = JPEG_QUALITY) -> None:
    """拡張子に応じて保存する。JPEG は背景を白で塗りつぶして不透明化する。"""
    ext = dst.suffix.lower()
    if ext in (".jpg", ".jpeg"):
        if img.mode == "RGBA":
            bg = Image.new("RGB", img.size, (255, 255, 255))
            bg.paste(img, mask=img.split()[3])
            img = bg
        else:
            img = img.convert("RGB")
        img.save(dst, format="JPEG", quality=quality, optimize=True)
    elif ext == ".webp":
        img.save(dst, format="WEBP", quality=quality)
    else:
        img.save(dst, format="PNG", optimize=True)


# ================================================================ 切り抜きタブ

class CropTab(ttk.Frame):
    HANDLE = 7          # ハンドルの当たり判定半径（px）
    MIN_SIZE = 8        # クロップ枠の最小サイズ（画像px）

    def __init__(self, master):
        super().__init__(master, padding=8)
        self.paths: list[Path] = []
        self.index = -1
        self.image: Image.Image | None = None
        self._photo: ImageTk.PhotoImage | None = None

        # 画像→キャンバス変換パラメータ
        self.scale = 1.0
        self.off_x = 0
        self.off_y = 0

        # クロップ枠（画像座標 x0,y0,x1,y1）
        self.rect: list[float] | None = None
        self.aspect: float | None = None

        # ドラッグ状態
        self._mode = None            # 'move' / 'resize'
        self._fixed = (0.0, 0.0)     # resize 時の固定コーナー（画像座標）
        self._move_off = (0.0, 0.0)

        # 表示キャッシュ（ドラッグ中に画像を再縮小しないため）
        self._disp = (0, 0)          # 縮小表示画像のサイズ
        self._resize_job = None      # <Configure> デバウンス用

        self._build()

    def _build(self):
        pad = {"padx": 6, "pady": 3}

        top = ttk.Frame(self)
        top.pack(fill="x")
        ttk.Button(top, text="画像を開く...", command=self._open).pack(side="left", **pad)
        ttk.Button(top, text="◀ 前", command=lambda: self._step(-1)).pack(side="left")
        ttk.Button(top, text="次 ▶", command=lambda: self._step(1)).pack(side="left", padx=(2, 6))
        self.name_var = tk.StringVar(value="（画像未選択）")
        ttk.Label(top, textvariable=self.name_var).pack(side="left", **pad)

        body = ttk.Frame(self)
        body.pack(fill="both", expand=True, pady=(4, 0))

        self.canvas = tk.Canvas(body, bg="#2b2b2b", highlightthickness=0,
                                width=720, height=480, cursor="tcross")
        self.canvas.pack(side="left", fill="both", expand=True)
        self.canvas.bind("<Configure>", self._on_canvas_resize)
        self.canvas.bind("<ButtonPress-1>", self._on_press)
        self.canvas.bind("<B1-Motion>", self._on_drag)

        side = ttk.Frame(body, width=170)
        side.pack(side="left", fill="y", padx=(8, 0))

        af = ttk.LabelFrame(side, text="アスペクト比")
        af.pack(fill="x")
        self.aspect_var = tk.StringVar(value=ASPECT_PRESETS[0][0])
        for name, _val in ASPECT_PRESETS:
            ttk.Radiobutton(af, text=name, value=name, variable=self.aspect_var,
                            command=self._on_aspect).pack(anchor="w", padx=6)
        crow = ttk.Frame(af)
        crow.pack(anchor="w", padx=6, pady=(2, 4))
        ttk.Radiobutton(crow, text="任意", value=CUSTOM_ASPECT,
                        variable=self.aspect_var,
                        command=self._on_aspect).pack(side="left")
        self.cw_var = tk.StringVar(value="16")
        self.ch_var = tk.StringVar(value="10")
        ttk.Entry(crow, textvariable=self.cw_var, width=4, justify="right").pack(side="left")
        ttk.Label(crow, text=":").pack(side="left")
        ttk.Entry(crow, textvariable=self.ch_var, width=4, justify="right").pack(side="left")

        ttk.Separator(af, orient="horizontal").pack(fill="x", padx=6, pady=2)
        self.flip_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(af, text="縦横を反転（4:3 → 3:4）",
                        variable=self.flip_var,
                        command=self._on_aspect).pack(anchor="w", padx=6, pady=(0, 4))

        self.size_var = tk.StringVar(value="")
        ttk.Label(side, textvariable=self.size_var).pack(anchor="w", pady=(6, 2))

        ttk.Button(side, text="この範囲で保存", command=self._save_current).pack(fill="x", pady=2)
        ttk.Button(side, text="全画像を同比率で\n中央切り抜き保存",
                   command=self._save_all_center).pack(fill="x", pady=2)

        of = ttk.LabelFrame(side, text="出力先")
        of.pack(fill="x", pady=(8, 0))
        self.out_dir = tk.StringVar()
        ttk.Entry(of, textvariable=self.out_dir).pack(fill="x", padx=4, pady=2)
        ttk.Button(of, text="参照...", command=self._browse_out).pack(fill="x", padx=4, pady=(0, 4))

        # 画像ファイル/フォルダのドラッグ＆ドロップ（tkinterdnd2 がある場合のみ）
        if getattr(self.winfo_toplevel(), "dnd_ok", False):
            for w in (self, self.canvas):
                w.drop_target_register(DND_FILES)
                w.dnd_bind("<<Drop>>", self._on_drop)

    # ---- ファイル操作 ----
    def _open(self):
        files = filedialog.askopenfilenames(
            title="画像を選択",
            filetypes=[("画像", "*.jpg *.jpeg *.png *.webp *.bmp *.gif"), ("すべて", "*.*")])
        if not files:
            return
        self._set_paths([Path(f) for f in files])

    def _set_paths(self, paths: list[Path]):
        self.paths = paths
        self.index = 0
        if not self.out_dir.get():
            self.out_dir.set(str(self.paths[0].parent / "cropped"))
        self._load_current()

    def _on_drop(self, event):
        """ドロップされたファイル/フォルダから対象画像を読み込む（フォルダは直下のみ）。"""
        paths: list[Path] = []
        for s in self.tk.splitlist(event.data):
            p = Path(s)
            if p.is_dir():
                paths += sorted((f for f in p.iterdir()
                                 if f.is_file() and f.suffix.lower() in TARGET_EXTS),
                                key=lambda f: f.name.lower())
            elif p.is_file() and p.suffix.lower() in TARGET_EXTS:
                paths.append(p)
        if not paths:
            messagebox.showinfo(APP_TITLE, "対応する画像ファイルがありません。")
            return event.action
        self._set_paths(paths)
        return event.action

    def _step(self, delta):
        if not self.paths:
            return
        self.index = (self.index + delta) % len(self.paths)
        self._load_current()

    def _load_current(self):
        path = self.paths[self.index]
        try:
            self.image = load_image(path)
        except Exception as e:
            messagebox.showerror(APP_TITLE, f"読み込み失敗: {path.name}\n{e}")
            return
        self.name_var.set(f"[{self.index + 1}/{len(self.paths)}] {path.name}"
                          f"  ({self.image.width}×{self.image.height})")
        self._reset_rect()
        self._redraw()

    def _browse_out(self):
        d = filedialog.askdirectory(title="出力フォルダを選択")
        if d:
            self.out_dir.set(d)

    # ---- アスペクト比 ----
    def _current_aspect(self) -> float | None:
        name = self.aspect_var.get()
        if name == CUSTOM_ASPECT:
            try:
                w = float(self.cw_var.get())
                h = float(self.ch_var.get())
                aspect = w / h if (w > 0 and h > 0) else None
            except ValueError:
                aspect = None
        else:
            aspect = dict(ASPECT_PRESETS).get(name)
        if aspect is not None and self.flip_var.get():
            aspect = 1 / aspect
        return aspect

    def _on_aspect(self):
        self.aspect = self._current_aspect()
        if self.image:
            self._reset_rect()
            self._redraw()

    def _reset_rect(self):
        """現在のアスペクト比で、画像中央に最大のクロップ枠を作る。"""
        if not self.image:
            return
        self.aspect = self._current_aspect()
        W, H = self.image.size
        if self.aspect is None:
            self.rect = [0.0, 0.0, float(W), float(H)]
        else:
            w, h = W, W / self.aspect
            if h > H:
                h, w = H, H * self.aspect
            x = (W - w) / 2
            y = (H - h) / 2
            self.rect = [x, y, x + w, y + h]

    # ---- 座標変換 ----
    def _i2c(self, ix, iy):
        return self.off_x + ix * self.scale, self.off_y + iy * self.scale

    def _c2i(self, cx, cy):
        return (cx - self.off_x) / self.scale, (cy - self.off_y) / self.scale

    def _clamp_i(self, ix, iy):
        W, H = self.image.size
        return min(max(ix, 0), W), min(max(iy, 0), H)

    # ---- 描画 ----
    # 重い処理（画像の縮小・PhotoImage 生成）は _redraw のみで行い、
    # ドラッグ中は _draw_overlay で枠だけを描き直す。

    def _on_canvas_resize(self, _e):
        # ウィンドウリサイズ中は <Configure> が連続発火するためデバウンス
        if self._resize_job is not None:
            self.after_cancel(self._resize_job)
        self._resize_job = self.after(40, self._redraw)

    def _redraw(self):
        self._resize_job = None
        self.canvas.delete("all")
        if not self.image:
            return
        cw = self.canvas.winfo_width() or 720
        ch = self.canvas.winfo_height() or 480
        W, H = self.image.size
        self.scale = min(cw / W, ch / H)
        disp_w, disp_h = max(1, round(W * self.scale)), max(1, round(H * self.scale))
        self.off_x = (cw - disp_w) / 2
        self.off_y = (ch - disp_h) / 2
        self._disp = (disp_w, disp_h)

        self._photo = ImageTk.PhotoImage(
            self.image.resize((disp_w, disp_h), Image.BILINEAR))
        self.canvas.create_image(self.off_x, self.off_y, anchor="nw", image=self._photo)
        self._draw_overlay()

    def _draw_overlay(self):
        """クロップ枠・網掛け・ハンドルのみ再描画する（軽量）。"""
        self.canvas.delete("ov")
        if not self.rect:
            return
        disp_w, disp_h = self._disp
        x0, y0 = self._i2c(self.rect[0], self.rect[1])
        x1, y1 = self._i2c(self.rect[2], self.rect[3])
        # 選択範囲外を暗くする（上下左右の帯）
        shade = "#000000"
        self.canvas.create_rectangle(self.off_x, self.off_y, self.off_x + disp_w, y0,
                                     fill=shade, stipple="gray50", width=0, tags="ov")
        self.canvas.create_rectangle(self.off_x, y1, self.off_x + disp_w,
                                     self.off_y + disp_h, fill=shade, stipple="gray50",
                                     width=0, tags="ov")
        self.canvas.create_rectangle(self.off_x, y0, x0, y1, fill=shade,
                                     stipple="gray50", width=0, tags="ov")
        self.canvas.create_rectangle(x1, y0, self.off_x + disp_w, y1, fill=shade,
                                     stipple="gray50", width=0, tags="ov")
        # 枠と三分割ガイド
        self.canvas.create_rectangle(x0, y0, x1, y1, outline="#ffffff", width=1, tags="ov")
        # 三分割ガイドは stipple だと縦線が座標次第で消えるため破線で描く
        # （gray25 はドットが x%4==1,3 の列にしか無く、縦1px線が空になり得る）
        for k in (1, 2):
            gx = x0 + (x1 - x0) * k / 3
            gy = y0 + (y1 - y0) * k / 3
            self.canvas.create_line(gx, y0, gx, y1, fill="#ffffff",
                                    dash=(3, 3), tags="ov")
            self.canvas.create_line(x0, gy, x1, gy, fill="#ffffff",
                                    dash=(3, 3), tags="ov")
        # コーナーハンドル
        for hx, hy in ((x0, y0), (x1, y0), (x1, y1), (x0, y1)):
            self.canvas.create_rectangle(hx - 4, hy - 4, hx + 4, hy + 4,
                                         fill="#ffffff", outline="#0078d7", tags="ov")

        rw = round(self.rect[2] - self.rect[0])
        rh = round(self.rect[3] - self.rect[1])
        self.size_var.set(f"選択: {rw} × {rh} px")

    # ---- マウス操作 ----
    def _on_press(self, e):
        if not self.image or not self.rect:
            if self.image:
                # 枠が無ければドラッグで新規作成
                ix, iy = self._clamp_i(*self._c2i(e.x, e.y))
                self.rect = [ix, iy, ix, iy]
                self._mode = "resize"
                self._fixed = (ix, iy)
            return
        corners = {
            0: (self.rect[0], self.rect[1]), 1: (self.rect[2], self.rect[1]),
            2: (self.rect[2], self.rect[3]), 3: (self.rect[0], self.rect[3]),
        }
        for idx, (ix, iy) in corners.items():
            cx, cy = self._i2c(ix, iy)
            if abs(e.x - cx) <= self.HANDLE and abs(e.y - cy) <= self.HANDLE:
                self._mode = "resize"
                self._fixed = corners[(idx + 2) % 4]     # 対角を固定
                return
        # 枠の内側なら移動、外側なら新規作成
        ix, iy = self._c2i(e.x, e.y)
        if self.rect[0] <= ix <= self.rect[2] and self.rect[1] <= iy <= self.rect[3]:
            self._mode = "move"
            self._move_off = (ix - self.rect[0], iy - self.rect[1])
        else:
            ix, iy = self._clamp_i(ix, iy)
            self.rect = [ix, iy, ix, iy]
            self._mode = "resize"
            self._fixed = (ix, iy)

    def _on_drag(self, e):
        if not self._mode or not self.image:
            return
        if self._mode == "move":
            self._do_move(e)
        else:
            self._do_resize(e)
        self._draw_overlay()

    def _do_move(self, e):
        W, H = self.image.size
        ix, iy = self._c2i(e.x, e.y)
        w = self.rect[2] - self.rect[0]
        h = self.rect[3] - self.rect[1]
        x0 = min(max(ix - self._move_off[0], 0), W - w)
        y0 = min(max(iy - self._move_off[1], 0), H - h)
        self.rect = [x0, y0, x0 + w, y0 + h]

    def _do_resize(self, e):
        W, H = self.image.size
        fx, fy = self._fixed
        mx, my = self._clamp_i(*self._c2i(e.x, e.y))
        dirx = 1 if mx >= fx else -1
        diry = 1 if my >= fy else -1
        w = abs(mx - fx)
        h = abs(my - fy)
        aspect = self._current_aspect()
        if aspect:
            if w / aspect >= h:
                h = w / aspect
            else:
                w = h * aspect
            availx = (W - fx) if dirx > 0 else fx
            availy = (H - fy) if diry > 0 else fy
            s = min(availx / w if w else 1, availy / h if h else 1, 1.0)
            w *= s
            h *= s
        nx, ny = fx + dirx * w, fy + diry * h
        x0, x1 = sorted((fx, nx))
        y0, y1 = sorted((fy, ny))
        if x1 - x0 >= self.MIN_SIZE and y1 - y0 >= self.MIN_SIZE:
            self.rect = [x0, y0, x1, y1]

    # ---- 保存 ----
    def _crop_box(self, rect):
        W, H = self.image.size
        x0 = max(0, round(rect[0]))
        y0 = max(0, round(rect[1]))
        x1 = min(W, round(rect[2]))
        y1 = min(H, round(rect[3]))
        return (x0, y0, x1, y1)

    def _out_path(self, src: Path) -> Path | None:
        d = self.out_dir.get().strip()
        if not d:
            messagebox.showerror(APP_TITLE, "出力先フォルダを指定してください。")
            return None
        out = Path(d)
        out.mkdir(parents=True, exist_ok=True)
        return out / f"{src.stem}_crop{src.suffix}"

    def _save_current(self):
        if not self.image or not self.rect:
            return
        src = self.paths[self.index]
        dst = self._out_path(src)
        if not dst:
            return
        box = self._crop_box(self.rect)
        if box[2] - box[0] < 1 or box[3] - box[1] < 1:
            messagebox.showerror(APP_TITLE, "選択範囲が小さすぎます。")
            return
        try:
            save_image(self.image.crop(box), dst)
            messagebox.showinfo(APP_TITLE, f"保存しました:\n{dst}")
        except Exception as e:
            messagebox.showerror(APP_TITLE, f"保存失敗:\n{e}")

    def _save_all_center(self):
        aspect = self._current_aspect()
        if aspect is None:
            messagebox.showinfo(APP_TITLE, "「自由」以外のアスペクト比を選択してください。")
            return
        if not self.paths:
            return
        ok = err = 0
        for src in self.paths:
            try:
                img = load_image(src)
                W, H = img.size
                w, h = W, W / aspect
                if h > H:
                    h, w = H, H * aspect
                x = (W - w) / 2
                y = (H - h) / 2
                dst = self._out_path(src)
                if not dst:
                    return
                save_image(img.crop((round(x), round(y), round(x + w), round(y + h))), dst)
                ok += 1
            except Exception:
                err += 1
        messagebox.showinfo(APP_TITLE, f"完了: 保存 {ok} / 失敗 {err}")


# ================================================================ 連結タブ

class CombineTab(ttk.Frame):
    def __init__(self, master):
        super().__init__(master, padding=8)
        self.paths: list[Path] = []
        self._photo: ImageTk.PhotoImage | None = None
        self._build()

    def _build(self):
        pad = {"padx": 6, "pady": 3}
        left = ttk.Frame(self)
        left.pack(side="left", fill="y")

        ttk.Label(left, text="画像リスト（上から順に連結）").pack(anchor="w")
        self.listbox = tk.Listbox(left, width=34, height=14,
                                  selectmode="extended", activestyle="none")
        self.listbox.pack(fill="y", expand=True)
        brow = ttk.Frame(left)
        brow.pack(fill="x", pady=3)
        ttk.Button(brow, text="追加", command=self._add).pack(side="left", padx=1)
        ttk.Button(brow, text="削除", command=self._remove).pack(side="left", padx=1)
        ttk.Button(brow, text="▲", width=3, command=lambda: self._move(-1)).pack(side="left", padx=1)
        ttk.Button(brow, text="▼", width=3, command=lambda: self._move(1)).pack(side="left", padx=1)
        ttk.Button(brow, text="クリア", command=self._clear).pack(side="left", padx=1)

        opt = ttk.LabelFrame(left, text="設定")
        opt.pack(fill="x", pady=(6, 0))

        # 並べ方
        ttk.Label(opt, text="並べ方:").grid(row=0, column=0, sticky="e", **pad)
        self.layout_var = tk.StringVar(value="horizontal")
        lrow = ttk.Frame(opt)
        lrow.grid(row=0, column=1, sticky="w")
        for text, val in (("横", "horizontal"), ("縦", "vertical"), ("グリッド", "grid")):
            ttk.Radiobutton(lrow, text=text, value=val, variable=self.layout_var,
                            command=self._on_layout).pack(side="left")

        # グリッド列数
        self.col_lbl = ttk.Label(opt, text="列数:")
        self.col_lbl.grid(row=1, column=0, sticky="e", **pad)
        self.columns_var = tk.IntVar(value=2)
        self.col_spin = ttk.Spinbox(opt, from_=1, to=20, width=6, textvariable=self.columns_var)
        self.col_spin.grid(row=1, column=1, sticky="w", **pad)

        # サイズ揃え
        ttk.Label(opt, text="サイズ揃え:").grid(row=2, column=0, sticky="e", **pad)
        self.norm_var = tk.StringVar(value="none")
        self.norm_combo = ttk.Combobox(opt, textvariable=self.norm_var, width=16,
                                       state="readonly")
        self.norm_combo.grid(row=2, column=1, sticky="w", **pad)
        self.norm_combo.bind("<<ComboboxSelected>>", lambda _e: self._on_norm())

        self.target_lbl = ttk.Label(opt, text="指定px:")
        self.target_lbl.grid(row=3, column=0, sticky="e", **pad)
        self.target_var = tk.IntVar(value=600)
        self.target_spin = ttk.Spinbox(opt, from_=1, to=20000, width=8,
                                       textvariable=self.target_var)
        self.target_spin.grid(row=3, column=1, sticky="w", **pad)

        # 整列
        self.align_lbl = ttk.Label(opt, text="整列:")
        self.align_lbl.grid(row=4, column=0, sticky="e", **pad)
        self.align_var = tk.StringVar()
        self.align_combo = ttk.Combobox(opt, textvariable=self.align_var, width=16,
                                        state="readonly")
        self.align_combo.grid(row=4, column=1, sticky="w", **pad)

        # 間隔・余白
        ttk.Label(opt, text="間隔px:").grid(row=5, column=0, sticky="e", **pad)
        self.spacing_var = tk.IntVar(value=0)
        ttk.Spinbox(opt, from_=0, to=2000, width=8, textvariable=self.spacing_var).grid(
            row=5, column=1, sticky="w", **pad)
        ttk.Label(opt, text="外余白px:").grid(row=6, column=0, sticky="e", **pad)
        self.padding_var = tk.IntVar(value=0)
        ttk.Spinbox(opt, from_=0, to=2000, width=8, textvariable=self.padding_var).grid(
            row=6, column=1, sticky="w", **pad)

        # 背景色
        ttk.Label(opt, text="背景色:").grid(row=7, column=0, sticky="e", **pad)
        crow = ttk.Frame(opt)
        crow.grid(row=7, column=1, sticky="w", **pad)
        self.bg_var = tk.StringVar(value="#ffffff")
        ttk.Entry(crow, textvariable=self.bg_var, width=9).pack(side="left")
        self.swatch = tk.Label(crow, width=2, bg="#ffffff", relief="solid", borderwidth=1)
        self.swatch.pack(side="left", padx=3)
        self.swatch.bind("<Button-1>", lambda _e: self._pick_color())
        self.bg_var.trace_add("write", lambda *_a: self._sync_swatch())
        self.transparent_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(opt, text="背景を透過（PNG）", variable=self.transparent_var).grid(
            row=8, column=0, columnspan=2, sticky="w", **pad)

        # 出力形式
        ttk.Label(opt, text="出力形式:").grid(row=9, column=0, sticky="e", **pad)
        frow = ttk.Frame(opt)
        frow.grid(row=9, column=1, sticky="w")
        self.format_var = tk.StringVar(value="PNG")
        for f in ("PNG", "JPEG", "WEBP"):
            ttk.Radiobutton(frow, text=f, value=f, variable=self.format_var).pack(side="left")

        act = ttk.Frame(left)
        act.pack(fill="x", pady=(8, 0))
        ttk.Button(act, text="プレビュー", command=self._preview).pack(side="left", padx=2)
        ttk.Button(act, text="連結して保存...", command=self._save).pack(side="left", padx=2)

        # プレビュー領域
        right = ttk.Frame(self)
        right.pack(side="left", fill="both", expand=True, padx=(8, 0))
        self.preview = tk.Canvas(right, bg="#2b2b2b", highlightthickness=0)
        self.preview.pack(fill="both", expand=True)
        self.info_var = tk.StringVar(value="画像を追加してプレビューしてください。")
        ttk.Label(right, textvariable=self.info_var).pack(anchor="w", pady=(4, 0))

        self._on_layout()

    # ---- リスト操作 ----
    def _add(self):
        files = filedialog.askopenfilenames(
            title="連結する画像を選択",
            filetypes=[("画像", "*.jpg *.jpeg *.png *.webp *.bmp *.gif"), ("すべて", "*.*")])
        for f in files:
            self.paths.append(Path(f))
            self.listbox.insert("end", Path(f).name)

    def _remove(self):
        for i in reversed(self.listbox.curselection()):
            self.listbox.delete(i)
            del self.paths[i]

    def _move(self, delta):
        sel = self.listbox.curselection()
        if len(sel) != 1:
            return
        i = sel[0]
        j = i + delta
        if not (0 <= j < len(self.paths)):
            return
        self.paths[i], self.paths[j] = self.paths[j], self.paths[i]
        name = self.listbox.get(i)
        self.listbox.delete(i)
        self.listbox.insert(j, name)
        self.listbox.selection_set(j)

    def _clear(self):
        self.listbox.delete(0, "end")
        self.paths.clear()

    # ---- 設定連動 ----
    def _on_layout(self):
        layout = self.layout_var.get()
        is_grid = layout == "grid"
        state = "normal" if is_grid else "disabled"
        self.col_spin.configure(state=state)

        if is_grid:
            self.norm_combo.configure(values=["元の最大に合わせる", "セルを指定px角に"])
            self._norm_map = {"元の最大に合わせる": "none", "セルを指定px角に": "fixed"}
            self.align_combo.configure(state="disabled")
            self.align_combo.set("")
        else:
            self.norm_combo.configure(
                values=["そのまま", "小さい方に揃える", "大きい方に揃える", "指定pxに揃える"])
            self._norm_map = {"そのまま": "none", "小さい方に揃える": "min",
                              "大きい方に揃える": "max", "指定pxに揃える": "fixed"}
            self.align_combo.configure(state="readonly")
            if layout == "horizontal":
                self.align_combo.configure(values=["上", "中央", "下"])
            else:
                self.align_combo.configure(values=["左", "中央", "右"])
            self.align_combo.current(0)
        self.norm_combo.current(0)
        self._on_norm()

    def _on_norm(self):
        code = getattr(self, "_norm_map", {}).get(self.norm_combo.get(), "none")
        st = "normal" if code == "fixed" else "disabled"
        self.target_spin.configure(state=st)

    def _pick_color(self):
        rgb, hx = colorchooser.askcolor(self.bg_var.get(), title="背景色")
        if hx:
            self.bg_var.set(hx)

    def _sync_swatch(self):
        try:
            self.swatch.configure(bg=self.bg_var.get())
        except tk.TclError:
            pass

    # ---- 連結 ----
    def _norm_code(self):
        return getattr(self, "_norm_map", {}).get(self.norm_combo.get(), "none")

    def _align_code(self):
        return {0: "start", 1: "center", 2: "end"}.get(self.align_combo.current(), "start")

    def _render(self) -> Image.Image:
        if len(self.paths) < 2:
            raise ValueError("画像を2枚以上追加してください。")
        imgs = [load_image(p) for p in self.paths]
        bg = parse_color(self.bg_var.get(), self.transparent_var.get())
        layout = self.layout_var.get()
        if layout == "grid":
            return combine_grid(imgs, max(1, self.columns_var.get()), self._norm_code(),
                                self.target_var.get(), self.spacing_var.get(),
                                self.padding_var.get(), bg)
        return combine_linear(imgs, layout == "horizontal", self._norm_code(),
                              self.target_var.get(), self._align_code(),
                              self.spacing_var.get(), self.padding_var.get(), bg)

    def _preview(self):
        try:
            result = self._render()
        except Exception as e:
            messagebox.showerror(APP_TITLE, str(e))
            return
        self._last = result
        cw = self.preview.winfo_width() or 600
        ch = self.preview.winfo_height() or 400
        disp = result.copy()
        disp.thumbnail((cw, ch), Image.LANCZOS)
        self._photo = ImageTk.PhotoImage(disp)
        self.preview.delete("all")
        self.preview.create_image(cw / 2, ch / 2, image=self._photo)
        self.info_var.set(f"連結結果: {result.width} × {result.height} px")

    def _save(self):
        try:
            result = self._render()
        except Exception as e:
            messagebox.showerror(APP_TITLE, str(e))
            return
        fmt = self.format_var.get().lower()
        ext = {"jpeg": ".jpg", "png": ".png", "webp": ".webp"}[fmt]
        dst = filedialog.asksaveasfilename(
            title="保存先", defaultextension=ext,
            initialfile=f"combined{ext}",
            filetypes=[(fmt.upper(), f"*{ext}")])
        if not dst:
            return
        try:
            save_image(result, Path(dst))
            self.info_var.set(f"保存しました: {dst}")
        except Exception as e:
            messagebox.showerror(APP_TITLE, f"保存失敗:\n{e}")


# ================================================================ リサイズタブ

class ResizeTab(ttk.Frame):
    """従来の一括リサイズツール。処理コアは image_resizer を再利用する。"""

    def __init__(self, master):
        super().__init__(master, padding=4)
        self.msg_queue: queue.Queue = queue.Queue()
        self.worker: threading.Thread | None = None
        self.cancel_flag = threading.Event()
        self._build()
        self._poll_queue()

    # ---- UI 構築（image_resizer.App._build_ui のタブ版） ----
    def _build(self):
        pad = {"padx": 8, "pady": 4}

        io = ttk.LabelFrame(self, text="フォルダ")
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

        conv = ttk.LabelFrame(self, text="変換設定")
        conv.pack(fill="x", **pad)

        size_row = ttk.Frame(conv)
        size_row.pack(fill="x", **pad)
        ttk.Label(size_row, text="サイズ:").pack(side="left")
        self.size_var = tk.IntVar(value=R.SIZE_PRESETS[0])
        for s in R.SIZE_PRESETS:
            ttk.Radiobutton(size_row, text=f"長辺 {s}px", value=s,
                            variable=self.size_var).pack(side="left", padx=6)
        ttk.Radiobutton(size_row, text="任意:", value=R.CUSTOM_SIZE,
                        variable=self.size_var).pack(side="left", padx=(6, 0))
        self.custom_size_var = tk.StringVar()
        custom_entry = ttk.Entry(size_row, textvariable=self.custom_size_var,
                                 width=7, justify="right")
        custom_entry.pack(side="left")
        ttk.Label(size_row, text="px").pack(side="left", padx=(2, 6))
        ttk.Radiobutton(size_row, text="変更しない", value=R.KEEP_SIZE,
                        variable=self.size_var).pack(side="left", padx=6)
        custom_entry.bind("<FocusIn>",
                          lambda _e: self.size_var.set(R.CUSTOM_SIZE))

        fmt_row = ttk.Frame(conv)
        fmt_row.pack(fill="x", **pad)
        ttk.Label(fmt_row, text="出力形式:").pack(side="left")
        self.format_var = tk.StringVar(value=R.FORMAT_KEEP)
        for label, value in R.FORMAT_LABELS:
            ttk.Radiobutton(fmt_row, text=label, value=value,
                            variable=self.format_var).pack(side="left", padx=6)
        ttk.Label(fmt_row, text="（JPGへの変換では透過部分を白で塗りつぶします）").pack(
            side="left", padx=(6, 0))

        algo_row = ttk.Frame(conv)
        algo_row.pack(fill="x", **pad)
        ttk.Label(algo_row, text="アルゴリズム:").pack(side="left")
        self.algo_var = tk.StringVar(value="Lanczos")
        for name in R.RESAMPLE_METHODS:
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

        name_f = ttk.LabelFrame(self, text="ファイル名オプション")
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

        dup = ttk.LabelFrame(self, text="出力先に同名ファイルがある場合")
        dup.pack(fill="x", **pad)
        self.overwrite_var = tk.BooleanVar(value=False)
        row = ttk.Frame(dup)
        row.pack(fill="x", **pad)
        ttk.Radiobutton(row, text="処理をスキップ", value=False,
                        variable=self.overwrite_var).pack(side="left", padx=6)
        ttk.Radiobutton(row, text="上書き", value=True,
                        variable=self.overwrite_var).pack(side="left", padx=6)

        run_row = ttk.Frame(self)
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

        self.log = scrolledtext.ScrolledText(self, height=8, state="disabled")
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
        if long_edge == R.CUSTOM_SIZE:
            try:
                long_edge = int(self.custom_size_var.get().strip())
            except ValueError:
                long_edge = 0
            if not (1 <= long_edge <= R.MAX_LONG_EDGE):
                messagebox.showerror(
                    APP_TITLE,
                    f"任意サイズには 1〜{R.MAX_LONG_EDGE} の整数を入力してください。")
                return

        opts = R.ConvertOptions(
            long_edge=long_edge,
            resample=R.RESAMPLE_METHODS[self.algo_var.get()],
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
        self._log(f"=== 変換開始: {R.describe_options(opts, self.algo_var.get())} ===")

        def report(kind, *args):
            self.msg_queue.put((kind, args))

        self.worker = threading.Thread(
            target=R.process_folder,
            args=(in_dir, out_dir, opts, report, self.cancel_flag.is_set),
            daemon=True,
        )
        self.worker.start()

    def _cancel(self):
        self.cancel_flag.set()
        self.cancel_btn.configure(state="disabled")

    # ---- ワーカーからの通知処理 ----
    def _poll_queue(self):
        if not self.winfo_exists():
            return
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


# ================================================================ アプリ本体

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.dnd_ok = False
        if TkinterDnD is not None:
            try:
                TkinterDnD._require(self)
                self.dnd_ok = True
            except (tk.TclError, RuntimeError):
                pass
        self.title(APP_TITLE)
        self.geometry("980x680")
        self.minsize(820, 560)
        try:
            self.option_add("*Font", ("Yu Gothic UI", 9))
        except tk.TclError:
            pass

        nb = ttk.Notebook(self)
        nb.pack(fill="both", expand=True)
        nb.add(ResizeTab(nb), text="  リサイズ  ")
        nb.add(CropTab(nb), text="  切り抜き  ")
        nb.add(CombineTab(nb), text="  連結  ")


if __name__ == "__main__":
    App().mainloop()
