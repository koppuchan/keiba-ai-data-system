/* あなたに合った競馬レース診断（スマホ優先）。質問は1画面1問、回答はタップだけ。 */
(function () {
    'use strict';

    var cfg = window.KEIBA_DIAG;
    var root = document.getElementById('keiba-diagnosis');
    if (!cfg || !root) {
        return;
    }

    var QUESTIONS = [
        {
            key: 's',
            title: '競馬で重視したいのは？',
            options: [['hit', '的中率を重視'], ['ana', '穴馬を狙いたい'], ['high', '高配当を狙いたい'], ['ai', 'AIの総合評価を重視']]
        },
        {
            key: 'p',
            title: 'どのような馬を狙いたいですか？',
            options: [['fav', '人気馬中心'], ['mid', '中穴中心'], ['long', '大穴中心'], ['any', '特にこだわらない']]
        },
        {
            key: 'm',
            title: '重視する予想材料は？',
            options: [['pace', '展開・先行力'], ['agari', '上がり・末脚'], ['jockey', '騎手・コース適性'], ['pedigree', '血統・調教'], ['ai', 'AI総合指数']]
        },
        {
            key: 'c',
            title: '予想したい競馬は？',
            options: [['central', '中央競馬'], ['local', '地方競馬'], ['both', 'どちらでもよい']]
        }
    ];

    var STORAGE_KEY = 'keiba_diag_last';
    var answers = {};
    var step = 0;

    /* ---------------------------------------------------------------- 計測 */

    function queryParam(name) {
        var match = new RegExp('[?&]' + name + '=([^&]*)').exec(window.location.search);
        return match ? decodeURIComponent(match[1].replace(/\+/g, ' ')) : '';
    }

    function referrerHost() {
        try {
            return document.referrer ? new URL(document.referrer).hostname : '';
        } catch (e) {
            return '';
        }
    }

    function track(name) {
        var gaName = 'diagnosis_' + name;
        var params = { event_category: 'diagnosis' };
        // 通常の診断結果と代替診断結果を区別できるよう、結果の種類を付ける（result_normal / result_fallback）。
        var kind = /_(normal|fallback)$/.exec(name);
        if (kind) {
            params.result_type = kind[1];
        }
        try {
            if (typeof window.gtag === 'function') {
                window.gtag('event', gaName, params);
            } else {
                // WP Rocket等でGA4のタグの読み込みが遅らされている場合に備え、gtag形式（arguments）でキューへ積む。
                // タグが読み込まれた時点で順に処理される。
                window.dataLayer = window.dataLayer || [];
                (function () { window.dataLayer.push(arguments); })('event', gaName, params);
            }
            if (window.dataLayer && typeof window.dataLayer.push === 'function') {
                window.dataLayer.push({ event: gaName }); // Googleタグマネージャー用
            }
        } catch (e) { /* 計測の失敗で診断を止めない */ }

        try {
            var body = JSON.stringify({ event: name, utm: queryParam('utm_source'), ref: referrerHost() });
            if (navigator.sendBeacon) {
                navigator.sendBeacon(cfg.eventUrl, new Blob([body], { type: 'application/json' }));
            } else {
                var xhr = new XMLHttpRequest();
                xhr.open('POST', cfg.eventUrl, true);
                xhr.setRequestHeader('Content-Type', 'application/json');
                xhr.send(body);
            }
        } catch (e) { /* 同上 */ }
    }

    /* ---------------------------------------------------------------- 本日の開催状況（選択画面に表示） */

    var statusPromise = null;

    function loadStatus() {
        if (!statusPromise) {
            statusPromise = fetch(cfg.statusUrl, { credentials: 'same-origin', cache: 'no-store' })
                .then(function (res) {
                    if (!res.ok) {
                        throw new Error('status ' + res.status);
                    }
                    return res.json();
                })
                .catch(function () {
                    return null; // 取得できなくても診断は続けられる（表示を省くだけ）。
                });
        }
        return statusPromise;
    }

    function statusText(info, isBoth) {
        if (!info) {
            return '';
        }
        if (isBoth) {
            return info.remaining > 0
                ? '本日診断できるレース ' + info.remaining + 'レース'
                : '本日はこれから診断できるレースがありません（あなたに合ったスタイルをご案内します）';
        }
        switch (info.state) {
            case 'open':
                return '本日開催中・診断できるレース ' + info.remaining + 'レース';
            case 'ended':
                return '本日の開催は終了しました';
            case 'preparing':
                return '本日のデータを準備中です';
            default:
                return '本日の開催はありません';
        }
    }

    /* ---------------------------------------------------------------- 描画の補助 */

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined && text !== null) {
            node.textContent = text;
        }
        return node;
    }

    function clear() {
        while (root.firstChild) {
            root.removeChild(root.firstChild);
        }
    }

    function button(label, className, onClick) {
        var b = el('button', className, label);
        b.type = 'button';
        b.addEventListener('click', onClick);
        return b;
    }

    function disclaimer() {
        return el('p', 'kdg-disclaimer', cfg.disclaimer);
    }

    function scrollTop() {
        try {
            root.scrollIntoView({ block: 'start' });
        } catch (e) { /* 古いブラウザは無視 */ }
    }

    /* ---------------------------------------------------------------- 画面 */

    function showIntro() {
        clear();
        var box = el('div', 'kdg-card kdg-intro');
        box.appendChild(el('p', 'kdg-catch', 'あなたに合うレースはどれ？'));
        box.appendChild(el('h2', 'kdg-title', '30秒でわかる無料競馬診断'));
        box.appendChild(el('p', 'kdg-lead', '4つの質問に答えるだけ。AI指数のデータから、あなたに合った注目レースや、あなたの競馬スタイルをご紹介します。'));
        box.appendChild(button('無料診断スタート', 'kdg-btn kdg-btn-primary', function () {
            track('start');
            answers = {};
            step = 0;
            showQuestion();
        }));
        box.appendChild(disclaimer());

        var saved = loadSaved();
        if (saved) {
            box.appendChild(button('前回の診断結果を見る', 'kdg-btn kdg-btn-link', function () {
                answers = saved;
                runDiagnosis(true);
            }));
        }
        root.appendChild(box);
    }

    function showQuestion() {
        clear();
        var q = QUESTIONS[step];
        var box = el('div', 'kdg-card');

        var progress = el('div', 'kdg-progress');
        var bar = el('div', 'kdg-progress-bar');
        bar.style.width = ((step + 1) / QUESTIONS.length * 100) + '%';
        progress.appendChild(bar);
        box.appendChild(progress);
        box.appendChild(el('p', 'kdg-step', '質問 ' + (step + 1) + ' / ' + QUESTIONS.length));
        box.appendChild(el('h2', 'kdg-question', q.title));

        var list = el('div', 'kdg-options');
        var optionButtons = {};
        q.options.forEach(function (option) {
            var optionButton = button(option[1], 'kdg-btn kdg-option', function () {
                answers[q.key] = option[0];
                if (step + 1 < QUESTIONS.length) {
                    step += 1;
                    showQuestion();
                } else {
                    runDiagnosis();
                }
            });
            optionButtons[option[0]] = optionButton;
            list.appendChild(optionButton);
        });
        box.appendChild(list);

        if (q.key === 'c') {
            // 中央・地方の選択画面に、本日の開催有無と診断できるレース数を表示する。
            loadStatus().then(function (status) {
                if (!status || !optionButtons.central || !list.parentNode) {
                    return;
                }
                [['central', false], ['local', false], ['both', true]].forEach(function (pair) {
                    var text = statusText(status[pair[0]], pair[1]);
                    if (text && optionButtons[pair[0]]) {
                        optionButtons[pair[0]].appendChild(el('span', 'kdg-option-sub', text));
                    }
                });
            });
        }

        if (step > 0) {
            box.appendChild(button('← 前の質問に戻る', 'kdg-btn kdg-btn-link', function () {
                step -= 1;
                showQuestion();
            }));
        }
        root.appendChild(box);
        scrollTop();
    }

    function showMessage(title, text, withRetry) {
        clear();
        var box = el('div', 'kdg-card');
        box.appendChild(el('h2', 'kdg-title', title));
        box.appendChild(el('p', 'kdg-lead', text));
        if (withRetry) {
            box.appendChild(button('条件を変えてもう一度診断する', 'kdg-btn kdg-btn-primary', showIntro));
        }
        box.appendChild(disclaimer());
        root.appendChild(box);
        scrollTop();
    }

    /**
     * restored=true は、前回の回答での再表示（診断を完了した数としては数えない）。
     * onlyIfUnlocked=true は、LINE解放済みの人にだけ前回の結果を自動表示する（未解放ならトップへ）。
     * 解放済みかどうかはHTMLに埋めず、サーバーの応答で判定する（ページキャッシュで他の人の状態が混ざらないように）。
     */
    function runDiagnosis(restored, onlyIfUnlocked, recheck) {
        clear();
        var box = el('div', 'kdg-card');
        box.appendChild(el('p', 'kdg-loading', 'AI指数のデータを確認しています…'));
        root.appendChild(box);

        var query = ['s', 'p', 'm', 'c'].map(function (k) {
            return k + '=' + encodeURIComponent(answers[k]);
        }).join('&');

        fetch(cfg.diagnosisUrl + (cfg.diagnosisUrl.indexOf('?') === -1 ? '?' : '&') + query, { credentials: 'same-origin' })
            .then(function (res) {
                if (!res.ok) {
                    throw new Error('status ' + res.status);
                }
                return res.json();
            })
            .then(function (data) {
                if (onlyIfUnlocked && !data.unlocked) {
                    showIntro();
                    return;
                }
                if (recheck) {
                    track('recheck'); // 条件を変えた再診断。診断完了の数には足さない。
                } else if (!restored) {
                    track('complete');
                }
                save(answers);
                if (!restored) {
                    track(data.status === 'fallback' ? 'result_fallback' : 'result_normal');
                }
                if (data.status === 'fallback') {
                    showFallback(data);
                } else {
                    showResult(data);
                }
            })
            .catch(function () {
                showMessage('診断を取得できませんでした', '通信に失敗しました。時間をおいてもう一度お試しください。', true);
            });
    }

    /** LINE友だち追加ボタン。kind（normal / fallback）で、通常の診断結果と代替診断結果のクリックを区別して計測する。 */
    function friendAddButton(label, className, kind) {
        if (!cfg.addFriendUrl) {
            return null;
        }
        var a = el('a', className, label);
        a.href = cfg.addFriendUrl;
        a.target = '_blank';
        a.rel = 'noopener';
        a.addEventListener('click', function () {
            track('line_click');
            track('line_click_' + kind);
        });
        return a;
    }

    var COURSE_AGAIN_LABELS = { central: '中央競馬で診断する', local: '地方競馬で診断する', both: '中央・地方の両方で診断する' };

    /**
     * 診断できるレースが無いとき（開催のない日、全レース終了後、データ準備中）の代替診断。
     * 回答を引き継いだまま、中央・地方の条件を変えて再診断できる。
     */
    function showFallback(data) {
        clear();
        var fb = data.fallback;
        var wrap = el('div', 'kdg-result');

        var head = el('div', 'kdg-card kdg-result-head');
        head.appendChild(el('p', 'kdg-catch', 'あなたに合った競馬スタイル'));
        head.appendChild(el('h2', 'kdg-type', '「' + fb.styleName + '」'));
        head.appendChild(el('p', 'kdg-lead', fb.lead));
        head.appendChild(el('p', 'kdg-note', '診断タイプ：' + data.type));
        wrap.appendChild(head);

        var notice = data.reason === 'preparing'
            ? '本日のレースデータを準備中のため、あなたの回答から競馬スタイルをお届けしています。'
            : 'いま診断できるレースがないため、あなたの回答から競馬スタイルをお届けしています。';
        var guide = el('div', 'kdg-card');
        guide.appendChild(el('p', 'kdg-note kdg-note-first', notice));
        guide.appendChild(el('p', 'kdg-reason-title', 'あなたに合った見方'));
        var ul = el('ul', 'kdg-reasons');
        fb.points.forEach(function (point) {
            ul.appendChild(el('li', null, point));
        });
        guide.appendChild(ul);
        guide.appendChild(el('p', 'kdg-lead', fb.next));
        wrap.appendChild(guide);

        // 再診断：これまでの回答はそのままに、中央・地方だけ変えてもう一度診断する。
        var again = el('div', 'kdg-card');
        again.appendChild(el('p', 'kdg-reason-title', '条件を変えて、もう一度診断する（回答はそのまま）'));
        ['central', 'local', 'both'].forEach(function (course) {
            if (course === answers.c) {
                return;
            }
            again.appendChild(button(COURSE_AGAIN_LABELS[course], 'kdg-btn kdg-btn-primary', function () {
                answers.c = course;
                runDiagnosis(false, false, true);
            }));
        });
        wrap.appendChild(again);

        // LINE登録への導線（代替診断にも必ず置く）。
        var cta = el('div', 'kdg-card kdg-cta');
        cta.appendChild(el('p', 'kdg-cta-title', 'LINE登録で、開催日のAI指数をチェック！'));
        cta.appendChild(el('p', 'kdg-lead', 'LINE公式アカウントを友だち追加すると、開催日のAI指数や注目馬などの詳細情報をご覧いただけます。'));
        var line = friendAddButton('LINE登録で詳細情報をチェック（無料）', 'kdg-btn kdg-btn-line', 'fallback');
        cta.appendChild(line || el('p', 'kdg-note', 'LINEのご案内は準備中です。'));
        var gen = el('a', 'kdg-btn kdg-btn-secondary', 'スマートAI予想ジェネレーターでレースを分析する →');
        gen.href = cfg.generatorUrl;
        gen.target = '_blank';
        gen.rel = 'noopener';
        cta.appendChild(gen);
        wrap.appendChild(cta);

        wrap.appendChild(button('最初から診断し直す', 'kdg-btn kdg-btn-link', showIntro));
        wrap.appendChild(disclaimer());
        root.appendChild(wrap);
        scrollTop();
    }

    function showResult(data) {
        clear();
        var wrap = el('div', 'kdg-result');

        var head = el('div', 'kdg-card kdg-result-head');
        head.appendChild(el('p', 'kdg-catch', 'あなたの診断タイプ'));
        head.appendChild(el('h2', 'kdg-type', '「' + data.type + '」'));
        if (data.relaxed) {
            head.appendChild(el('p', 'kdg-note', '人気の条件に合うレースがなかったため、人気の条件を外してお選びしています。'));
        }
        head.appendChild(el('p', 'kdg-note', '注目度の目安：AI指数・要素別評価（偏差値、平均50）をもとに、S＝60以上、A＝55以上、B＝50以上です。'));
        wrap.appendChild(head);

        var details = [];
        data.items.forEach(function (item, index) {
            var card = el('div', 'kdg-card kdg-race' + (index === 0 ? ' kdg-race-main' : ''));
            card.appendChild(el('p', 'kdg-catch', index === 0 ? 'あなたに合ったレース' : '他の注目レース'));

            var title = el('h3', 'kdg-race-title');
            title.appendChild(document.createTextNode(item.venue + ' ' + item.raceNumber + 'R'));
            card.appendChild(title);

            var meta = el('p', 'kdg-race-meta');
            var rank = el('span', 'kdg-rank kdg-rank-' + item.rank, '注目度 ' + item.rank);
            meta.appendChild(rank);
            meta.appendChild(document.createTextNode(' 発走 ' + item.startTime + (item.raceName ? '　' + item.raceName : '')));
            card.appendChild(meta);

            card.appendChild(el('p', 'kdg-reason-title', 'このレースを選んだ理由'));
            var ul = el('ul', 'kdg-reasons');
            item.reasons.forEach(function (reason) {
                ul.appendChild(el('li', null, reason));
            });
            card.appendChild(ul);

            var detail = el('div', 'kdg-detail');
            detail.hidden = true;
            card.appendChild(detail);
            details.push({ node: detail, item: item });

            card.appendChild(el('a', 'kdg-link', 'この開催場の無料予想（AI指数ダイジェスト）を見る →')).setAttribute('href', item.link);
            wrap.appendChild(card);
        });

        var cta = el('div', 'kdg-card kdg-cta');
        wrap.appendChild(cta);

        function renderDetails() {
            details.forEach(function (d) {
                while (d.node.firstChild) {
                    d.node.removeChild(d.node.firstChild);
                }
                d.node.appendChild(el('p', 'kdg-reason-title', '詳細な予想情報'));
                var table = el('table', 'kdg-table');
                var thead = el('thead');
                var hr = el('tr');
                ['馬番', '馬名', 'AI指数', '人気', '区分'].forEach(function (h) {
                    hr.appendChild(el('th', null, h));
                });
                thead.appendChild(hr);
                table.appendChild(thead);
                var tbody = el('tbody');
                (d.item.detail || []).forEach(function (row) {
                    var tr = el('tr');
                    tr.appendChild(el('td', null, row.umaban === null ? '-' : String(row.umaban)));
                    tr.appendChild(el('td', null, row.horseName || '-'));
                    tr.appendChild(el('td', null, row.aiIndex === null ? '-' : String(row.aiIndex)));
                    tr.appendChild(el('td', null, row.ninki === null ? '-' : row.ninki + '番人気'));
                    tr.appendChild(el('td', null, (row.roles || []).join('・')));
                    tbody.appendChild(tr);
                });
                table.appendChild(tbody);
                var scroll = el('div', 'kdg-table-scroll');
                scroll.appendChild(table);
                d.node.appendChild(scroll);
                d.node.hidden = false;
            });
        }

        if (data.unlocked) {
            track('detail_view');
            renderDetails();
            cta.appendChild(el('p', 'kdg-cta-title', '詳細をご覧いただけます'));
            var more = friendAddButton('LINE友だち追加がまだの方はこちら（無料）', 'kdg-btn kdg-btn-line', 'normal');
            if (more) {
                cta.appendChild(more);
            }
        } else {
            cta.appendChild(el('p', 'kdg-cta-title', 'あなた向けの注目馬・AI指数を無料公開中！'));
            cta.appendChild(el('p', 'kdg-lead', '本命馬・注目穴馬・危険な人気馬・AI指数などの詳細は、LINE登録後にご覧いただけます。'));
            var line = friendAddButton('LINE登録で診断結果の詳細をチェック', 'kdg-btn kdg-btn-line', 'normal');
            var open = button('本命馬・穴馬・AI指数などの詳細を見る', 'kdg-btn kdg-btn-primary', function () {
                track('detail_click');
                open.hidden = true;
                panel.hidden = false;
            });
            cta.appendChild(open);

            var panel = el('div', 'kdg-line-panel');
            panel.hidden = true;
            panel.appendChild(el('p', 'kdg-lead', 'LINE公式アカウントを友だち追加すると、診断結果の詳細（本命馬・穴馬・AI指数など）をご覧いただけます。'));
            if (line) {
                panel.appendChild(line);
            } else {
                panel.appendChild(el('p', 'kdg-note', 'LINEのご案内は準備中です。'));
            }
            panel.appendChild(el('p', 'kdg-note', '友だち追加後、LINEに届く専用リンクから、この診断の詳細が開きます。すでに友だち追加済みの方は、LINEのトークにある専用リンクからお進みください。'));
            cta.appendChild(panel);
        }

        var gen = el('a', 'kdg-btn kdg-btn-secondary', 'スマートAI予想ジェネレーターでレースを分析する →');
        gen.href = cfg.generatorUrl;
        gen.target = '_blank';
        gen.rel = 'noopener';
        cta.appendChild(gen);

        wrap.appendChild(button('もう一度診断する', 'kdg-btn kdg-btn-link', showIntro));
        wrap.appendChild(disclaimer());
        root.appendChild(wrap);
        scrollTop();
    }

    /* ---------------------------------------------------------------- 前回の結果（LINE登録後に戻ってきたとき用） */

    function save(a) {
        try {
            window.localStorage.setItem(STORAGE_KEY, JSON.stringify({ a: a, t: Date.now() }));
        } catch (e) { /* 保存できなくても診断は続けられる */ }
    }

    function loadSaved() {
        try {
            var raw = window.localStorage.getItem(STORAGE_KEY);
            if (!raw) {
                return null;
            }
            var saved = JSON.parse(raw);
            if (!saved || !saved.a || Date.now() - saved.t > 24 * 60 * 60 * 1000) {
                return null; // 古い結果は使わない（その日のレースが変わるため）
            }
            var ok = QUESTIONS.every(function (q) {
                return q.options.some(function (o) { return o[0] === saved.a[q.key]; });
            });
            return ok ? saved.a : null;
        } catch (e) {
            return null;
        }
    }

    /* ---------------------------------------------------------------- 開始 */

    track('view');
    var savedAtStart = loadSaved();
    if (savedAtStart) {
        // LINE登録後に専用リンクから戻ってきた人は、診断をやり直さず結果の詳細へ。
        answers = savedAtStart;
        runDiagnosis(true, true);
    } else {
        showIntro();
    }
})();
