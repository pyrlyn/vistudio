import 'package:flutter/material.dart';

import 'widgets/greeting_card.dart';
import 'widgets/status_badge.dart';

void main() => runApp(const PreviewSampleApp());

class PreviewSampleApp extends StatelessWidget {
  const PreviewSampleApp({super.key});

  @override
  Widget build(BuildContext context) {
    return const MaterialApp(
      home: Scaffold(
        body: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [GreetingCard(), StatusBadge()],
          ),
        ),
      ),
    );
  }
}
