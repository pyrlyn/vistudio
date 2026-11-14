import 'package:dart_console/dart_console.dart';
import 'package:test/test.dart';

void main() {
  group('shapes', () {
    test('rectangle area', () {
      expect(Rectangle(2, 3).area, 6);
    });

    test('total area', () {
      expect(totalArea([Rectangle(2, 3), Circle(1)]), 9.14);
    });
  });

  test('add', () {
    expect(add(2, 3), 5);
  });
}
