import 'package:flutter_counter/main.dart' as app;
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:material_ui/material_ui.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('counter app', () {
    testWidgets('tapping the button increments the counter', (tester) async {
      app.main();
      await tester.pumpAndSettle();

      expect(find.text('0'), findsOneWidget);
      await tester.tap(find.byTooltip('Increment'));
      await tester.pumpAndSettle();
      expect(find.text('1'), findsOneWidget);
    });
  });
}
