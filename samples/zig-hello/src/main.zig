const std = @import("std");
const shapes = @import("shapes.zig");

pub fn main() void {
    const square = shapes.Rect{ .width = 3, .height = 3 };
    std.debug.print("area {d}, perimeter {d}\n", .{ square.area(), square.perimeter() });
    std.debug.print("sum {d}\n", .{add(2, 40)});
}

pub fn add(a: i32, b: i32) i32 {
    return a + b;
}

test "add" {
    try std.testing.expectEqual(@as(i32, 42), add(2, 40));
}
