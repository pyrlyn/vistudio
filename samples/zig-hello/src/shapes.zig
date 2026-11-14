//! Shapes used by main.zig: struct methods for CodeLens and navigation checks.

pub const Rect = struct {
    width: u32,
    height: u32,

    pub fn area(self: Rect) u32 {
        return self.width * self.height;
    }

    pub fn perimeter(self: Rect) u32 {
        return 2 * (self.width + self.height);
    }
};
