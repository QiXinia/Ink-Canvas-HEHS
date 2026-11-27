import assert from 'node:assert/strict';
import test from 'node:test';
import { InkDocument, InkPoint, InkStroke, StrokeAttributes, matrixFromArray } from '@inkcanvas/core';
import { normalizePenOffset } from '../harmony/core/src/main/ets/input/TouchCalibration.ts';
import { stringifyIcink } from '../harmony/core/src/main/ets/persistence/IcinkCodec.ts';
import { InkController } from '../harmony/ink/src/main/ets/InkController.ts';
import { PointerKind, PointerSample } from '../harmony/ink/src/main/ets/ControllerTypes.ts';
import { CommittedStrokeCommands, sceneCommandForStroke } from '../harmony/ink/src/main/ets/SceneBuilder.ts';

test('display smoothing preserves pointer samples, undo and exported ink', () => {
  function write(smooth) {
    const controller = new InkController();
    const sample = (x, y, time) => new PointerSample(1, x, y, 0.5, 0, 0, PointerKind.Pen, time);
    controller.Invalidated = () => {
      const live = controller.ActiveStroke;
      if (live !== null) sceneCommandForStroke(live, smooth);
    };
    controller.onPointerDown(sample(10, 10, 0));
    controller.onPointerMove(sample(30, 10, 20));
    controller.onPointerMove(sample(34, 50, 40));
    controller.onPointerUp(sample(55, 60, 60));
    assert.equal(controller.Document.Count, 1);
    const stroke = controller.Document.Strokes[0];
    const command = sceneCommandForStroke(stroke, smooth);
    const saved = stringifyIcink(controller.Document.Strokes);
    controller.Undo();
    assert.equal(controller.Document.Count, 0);
    controller.Redo();
    assert.equal(stringifyIcink(controller.Document.Strokes), saved);
    return { command, saved };
  }
  const original = write(false);
  const smooth = write(true);
  assert.equal(smooth.saved, original.saved);
  assert.ok(smooth.command.Contours.length > original.command.Contours.length);
});

test('a long board keeps existing commands through unchanged frames and a new stroke', () => {
  const cache = new CommittedStrokeCommands();
  const document = new InkDocument();
  const makeStroke = (x) => {
    const stroke = new InkStroke(new StrokeAttributes());
    stroke.restorePoints([
      new InkPoint(x, 0, 0.5, 2), new InkPoint(x + 5, 0, 0.5, 2), new InkPoint(x + 10, 0, 0.5, 2)
    ]);
    return stroke;
  };
  document.addRange(Array.from({ length: 2050 }, (_, i) => makeStroke(i * 12)));
  cache.updateDocument(document);
  const first = document.Strokes.map(stroke => cache.get(stroke, false));
  document.add(makeStroke(30000));
  cache.updateDocument(document);
  first.forEach((command, i) => assert.strictEqual(cache.get(document.Strokes[i], false), command));

  const edited = document.Strokes[0];
  edited.restorePoints([new InkPoint(0, 0, 0.5, 2), new InkPoint(5, 4, 0.5, 2), new InkPoint(10, 0, 0.5, 2)]);
  const next = cache.get(edited, false);
  assert.notDeepEqual(next.Contours, first[0].Contours);
  assert.notStrictEqual(cache.get(edited, false, matrixFromArray([1, 0, 0, 1, 3, 4])), next);
  assert.strictEqual(cache.get(document.Strokes[1], false), first[1]);

  const nextDocument = new InkDocument();
  nextDocument.add(edited);
  cache.updateDocument(nextDocument);
  assert.notStrictEqual(cache.get(edited, false), next);
});

test('stored pen offsets preserve half-unit calibration and handle old preferences', () => {
  assert.equal(normalizePenOffset(undefined), 0);
  assert.equal(normalizePenOffset('5'), 0);
  assert.equal(normalizePenOffset(Infinity), 0);
  assert.equal(normalizePenOffset(-1.5), -1.5);
  assert.equal(normalizePenOffset(60), 50);
  assert.equal(normalizePenOffset(-60), -50);
});
