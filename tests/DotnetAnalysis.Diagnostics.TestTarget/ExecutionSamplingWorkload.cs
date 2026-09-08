using System.Runtime.CompilerServices;

internal sealed class ExecutionSamplingWorkload
{
    private const int WorkerCount = 8;
    private static long s_completedOperations;
    private static long s_publishedCompletedOperations;
    private static int s_lastConsumedPath;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task[] _workers;

    private ExecutionSamplingWorkload(CancellationTokenSource cancellation, Task[] workers)
    {
        _cancellation = cancellation;
        _workers = workers;
    }

    public static ExecutionSamplingWorkload Start()
    {
        var cancellation = new CancellationTokenSource();
        var workers = Enumerable.Range(0, WorkerCount)
            .Select(_ => Task.Factory.StartNew(
                () => RunWorker(cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        return new ExecutionSamplingWorkload(cancellation, workers);
    }

    public async Task StopAsync()
    {
        _cancellation.Cancel();
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunWorker(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Path000();
            Path001();
            Path002();
            Path003();
            Path004();
            Path005();
            Path006();
            Path007();
            Path008();
            Path009();
            Path010();
            Path011();
            Path012();
            Path013();
            Path014();
            Path015();
            Path016();
            Path017();
            Path018();
            Path019();
            Path020();
            Path021();
            Path022();
            Path023();
            Path024();
            Path025();
            Path026();
            Path027();
            Path028();
            Path029();
            Path030();
            Path031();
            Path032();
            Path033();
            Path034();
            Path035();
            Path036();
            Path037();
            Path038();
            Path039();
            Path040();
            Path041();
            Path042();
            Path043();
            Path044();
            Path045();
            Path046();
            Path047();
            Path048();
            Path049();
            Path050();
            Path051();
            Path052();
            Path053();
            Path054();
            Path055();
            Path056();
            Path057();
            Path058();
            Path059();
            Path060();
            Path061();
            Path062();
            Path063();
            Path064();
            Path065();
            Path066();
            Path067();
            Path068();
            Path069();
            Path070();
            Path071();
            Path072();
            Path073();
            Path074();
            Path075();
            Path076();
            Path077();
            Path078();
            Path079();
            Path080();
            Path081();
            Path082();
            Path083();
            Path084();
            Path085();
            Path086();
            Path087();
            Path088();
            Path089();
            Path090();
            Path091();
            Path092();
            Path093();
            Path094();
            Path095();
            Path096();
            Path097();
            Path098();
            Path099();
            Path100();
            Path101();
            Path102();
            Path103();
            Path104();
            Path105();
            Path106();
            Path107();
            Path108();
            Path109();
            Path110();
            Path111();
            Path112();
            Path113();
            Path114();
            Path115();
            Path116();
            Path117();
            Path118();
            Path119();
            Path120();
            Path121();
            Path122();
            Path123();
            Path124();
            Path125();
            Path126();
            Path127();
            Path128();
            Path129();
            Path130();
            Path131();
            Path132();
            Path133();
            Path134();
            Path135();
            Path136();
            Path137();
            Path138();
            Path139();
            Path140();
            Path141();
            Path142();
            Path143();
            Path144();
            Path145();
            Path146();
            Path147();
            Path148();
            Path149();
            Path150();
            Path151();
            Path152();
            Path153();
            Path154();
            Path155();
            Path156();
            Path157();
            Path158();
            Path159();
            Path160();
            Path161();
            Path162();
            Path163();
            Path164();
            Path165();
            Path166();
            Path167();
            Path168();
            Path169();
            Path170();
            Path171();
            Path172();
            Path173();
            Path174();
            Path175();
            Path176();
            Path177();
            Path178();
            Path179();
            Path180();
            Path181();
            Path182();
            Path183();
            Path184();
            Path185();
            Path186();
            Path187();
            Path188();
            Path189();
            Path190();
            Path191();
            Path192();
            Path193();
            Path194();
            Path195();
            Path196();
            Path197();
            Path198();
            Path199();
            var completed = Interlocked.Add(ref s_completedOperations, 200);
            Volatile.Write(ref s_publishedCompletedOperations, completed);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path000() => Consume(0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path001() => Consume(1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path002() => Consume(2);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path003() => Consume(3);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path004() => Consume(4);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path005() => Consume(5);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path006() => Consume(6);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path007() => Consume(7);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path008() => Consume(8);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path009() => Consume(9);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path010() => Consume(10);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path011() => Consume(11);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path012() => Consume(12);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path013() => Consume(13);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path014() => Consume(14);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path015() => Consume(15);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path016() => Consume(16);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path017() => Consume(17);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path018() => Consume(18);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path019() => Consume(19);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path020() => Consume(20);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path021() => Consume(21);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path022() => Consume(22);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path023() => Consume(23);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path024() => Consume(24);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path025() => Consume(25);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path026() => Consume(26);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path027() => Consume(27);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path028() => Consume(28);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path029() => Consume(29);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path030() => Consume(30);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path031() => Consume(31);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path032() => Consume(32);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path033() => Consume(33);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path034() => Consume(34);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path035() => Consume(35);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path036() => Consume(36);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path037() => Consume(37);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path038() => Consume(38);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path039() => Consume(39);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path040() => Consume(40);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path041() => Consume(41);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path042() => Consume(42);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path043() => Consume(43);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path044() => Consume(44);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path045() => Consume(45);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path046() => Consume(46);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path047() => Consume(47);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path048() => Consume(48);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path049() => Consume(49);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path050() => Consume(50);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path051() => Consume(51);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path052() => Consume(52);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path053() => Consume(53);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path054() => Consume(54);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path055() => Consume(55);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path056() => Consume(56);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path057() => Consume(57);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path058() => Consume(58);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path059() => Consume(59);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path060() => Consume(60);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path061() => Consume(61);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path062() => Consume(62);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path063() => Consume(63);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path064() => Consume(64);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path065() => Consume(65);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path066() => Consume(66);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path067() => Consume(67);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path068() => Consume(68);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path069() => Consume(69);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path070() => Consume(70);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path071() => Consume(71);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path072() => Consume(72);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path073() => Consume(73);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path074() => Consume(74);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path075() => Consume(75);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path076() => Consume(76);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path077() => Consume(77);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path078() => Consume(78);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path079() => Consume(79);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path080() => Consume(80);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path081() => Consume(81);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path082() => Consume(82);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path083() => Consume(83);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path084() => Consume(84);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path085() => Consume(85);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path086() => Consume(86);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path087() => Consume(87);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path088() => Consume(88);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path089() => Consume(89);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path090() => Consume(90);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path091() => Consume(91);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path092() => Consume(92);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path093() => Consume(93);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path094() => Consume(94);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path095() => Consume(95);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path096() => Consume(96);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path097() => Consume(97);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path098() => Consume(98);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path099() => Consume(99);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path100() => Consume(100);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path101() => Consume(101);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path102() => Consume(102);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path103() => Consume(103);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path104() => Consume(104);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path105() => Consume(105);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path106() => Consume(106);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path107() => Consume(107);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path108() => Consume(108);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path109() => Consume(109);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path110() => Consume(110);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path111() => Consume(111);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path112() => Consume(112);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path113() => Consume(113);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path114() => Consume(114);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path115() => Consume(115);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path116() => Consume(116);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path117() => Consume(117);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path118() => Consume(118);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path119() => Consume(119);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path120() => Consume(120);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path121() => Consume(121);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path122() => Consume(122);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path123() => Consume(123);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path124() => Consume(124);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path125() => Consume(125);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path126() => Consume(126);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path127() => Consume(127);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path128() => Consume(128);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path129() => Consume(129);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path130() => Consume(130);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path131() => Consume(131);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path132() => Consume(132);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path133() => Consume(133);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path134() => Consume(134);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path135() => Consume(135);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path136() => Consume(136);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path137() => Consume(137);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path138() => Consume(138);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path139() => Consume(139);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path140() => Consume(140);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path141() => Consume(141);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path142() => Consume(142);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path143() => Consume(143);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path144() => Consume(144);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path145() => Consume(145);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path146() => Consume(146);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path147() => Consume(147);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path148() => Consume(148);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path149() => Consume(149);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path150() => Consume(150);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path151() => Consume(151);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path152() => Consume(152);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path153() => Consume(153);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path154() => Consume(154);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path155() => Consume(155);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path156() => Consume(156);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path157() => Consume(157);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path158() => Consume(158);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path159() => Consume(159);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path160() => Consume(160);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path161() => Consume(161);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path162() => Consume(162);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path163() => Consume(163);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path164() => Consume(164);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path165() => Consume(165);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path166() => Consume(166);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path167() => Consume(167);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path168() => Consume(168);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path169() => Consume(169);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path170() => Consume(170);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path171() => Consume(171);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path172() => Consume(172);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path173() => Consume(173);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path174() => Consume(174);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path175() => Consume(175);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path176() => Consume(176);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path177() => Consume(177);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path178() => Consume(178);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path179() => Consume(179);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path180() => Consume(180);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path181() => Consume(181);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path182() => Consume(182);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path183() => Consume(183);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path184() => Consume(184);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path185() => Consume(185);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path186() => Consume(186);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path187() => Consume(187);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path188() => Consume(188);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path189() => Consume(189);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path190() => Consume(190);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path191() => Consume(191);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path192() => Consume(192);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path193() => Consume(193);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path194() => Consume(194);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path195() => Consume(195);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path196() => Consume(196);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path197() => Consume(197);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path198() => Consume(198);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Path199() => Consume(199);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(int path) => Volatile.Write(ref s_lastConsumedPath, path);
}
