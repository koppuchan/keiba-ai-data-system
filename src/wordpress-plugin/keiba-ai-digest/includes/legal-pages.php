<?php
/**
 * 会社概要・特定商取引法に基づく表記・プライバシーポリシー・利用規約の固定ページ。
 *
 * お客様（運営者）から提供された文面を、そのまま固定ページとして用意する。広告の審査で、診断ページ（LP）から
 * これらのページへ辿れることが求められたため、診断ページの下にもリンクを出す。
 *
 * 作成は「無い場合だけ」で、既にある固定ページ（運営者がエディタで編集したもの）は上書きしない。
 * 内容の変更は、WordPressの固定ページ編集画面から行う。
 */

if (!defined('ABSPATH')) {
    exit;
}

define('KEIBA_LEGAL_VERSION', '1');

/** 固定ページの定義: キー => array(スラッグ, タイトル, 本文を返す関数名) */
function keiba_legal_pages()
{
    return array(
        'company' => array('company', '会社概要（運営者情報）', 'keiba_legal_content_company'),
        'tokushoho' => array('tokushoho', '特定商取引法に基づく表記', 'keiba_legal_content_tokushoho'),
        'privacy' => array('privacy-policy', 'プライバシーポリシー', 'keiba_legal_content_privacy'),
        'terms' => array('terms', '利用規約', 'keiba_legal_content_terms'),
    );
}

/** 同じスラッグの固定ページが無ければ作る。 */
function keiba_legal_ensure_pages()
{
    $ids = (array) get_option('keiba_ai_digest_legal_page_ids', array());
    foreach (keiba_legal_pages() as $key => $def) {
        list($slug, $title, $content_fn) = $def;

        $id = isset($ids[$key]) ? (int) $ids[$key] : 0;
        if ($id && get_post_status($id) && get_post_status($id) !== 'trash') {
            continue;
        }

        $existing = get_page_by_path($slug, OBJECT, 'page');
        if ($existing && $existing->post_status === 'publish') {
            $ids[$key] = (int) $existing->ID; // 運営者が先に公開していた場合は、それを使う（上書きしない）。
            continue;
        }
        // WordPressが最初から用意する下書きの「プライバシーポリシー」（未編集の見本）は、この文面で置き換えて公開する。
        // それ以外の非公開ページ（運営者が書きかけのもの）は触らず、別のスラッグで新しく作る。
        if ($existing && in_array($existing->post_status, array('draft', 'auto-draft'), true)
            && (int) get_option('wp_page_for_privacy_policy', 0) === (int) $existing->ID) {
            wp_update_post(array(
                'ID' => $existing->ID,
                'post_status' => 'publish',
                'post_title' => $title,
                'post_content' => call_user_func($content_fn),
            ));
            $ids[$key] = (int) $existing->ID;
            continue;
        }

        $new_id = wp_insert_post(array(
            'post_type' => 'page',
            'post_status' => 'publish',
            'post_title' => $title,
            'post_name' => $slug,
            'post_content' => call_user_func($content_fn),
        ));
        if (!is_wp_error($new_id) && $new_id) {
            $ids[$key] = (int) $new_id;
            if ($key === 'privacy' && !get_option('wp_page_for_privacy_policy')) {
                update_option('wp_page_for_privacy_policy', (int) $new_id); // WordPress標準のプライバシーポリシー設定にも反映。
            }
        }
    }
    update_option('keiba_ai_digest_legal_page_ids', $ids, false);
}

add_action('init', function () {
    if (get_option('keiba_ai_digest_legal_pages_version') !== KEIBA_LEGAL_VERSION) {
        keiba_legal_ensure_pages();
        update_option('keiba_ai_digest_legal_pages_version', KEIBA_LEGAL_VERSION, false);
    }
});

/** 診断ページの下に出す、4つのページへのリンク。ページが無い（削除された）ものは出さない。 */
function keiba_legal_footer_links_html()
{
    $ids = (array) get_option('keiba_ai_digest_legal_page_ids', array());
    $labels = array(
        'company' => '会社概要',
        'tokushoho' => '特定商取引法に基づく表記',
        'privacy' => 'プライバシーポリシー',
        'terms' => '利用規約',
    );
    $items = array();
    foreach ($labels as $key => $label) {
        $id = isset($ids[$key]) ? (int) $ids[$key] : 0;
        if ($id && get_post_status($id) === 'publish') {
            $items[] = '<li><a href="' . esc_url(get_permalink($id)) . '">' . esc_html($label) . '</a></li>';
        }
    }
    if (!$items) {
        return '';
    }
    return '<nav class="keiba-legal-links" aria-label="サイトの情報"><ul>' . implode('', $items) . '</ul></nav>';
}

/** 表（項目 / 内容）を作る。内容は呼び出し側で作ったHTML。 */
function keiba_legal_table($rows)
{
    $html = '<table class="keiba-legal-table"><thead><tr><th>項目</th><th>内容</th></tr></thead><tbody>';
    foreach ($rows as $row) {
        $html .= '<tr><th scope="row">' . $row[0] . '</th><td>' . $row[1] . '</td></tr>';
    }
    return $html . '</tbody></table>';
}

function keiba_legal_list($items)
{
    return '<ul>' . implode('', array_map(function ($i) {
        return '<li>' . $i . '</li>';
    }, $items)) . '</ul>';
}

/* ---------------------------------------------------------------- 会社概要 */

function keiba_legal_content_company()
{
    $mail = 'torunedojingmayuxiangxuanfeng@gmail.com';
    $html = '<p>当サイト「今日の狙い目レース診断」は、プロによる競馬重賞無料予想運営事務局が運営する競馬情報サービスです。</p>';
    $html .= keiba_legal_table(array(
        array('運営名称', 'プロによる競馬重賞無料予想運営事務局'),
        array('運営責任者', '笛田 千雄'),
        array('所在地', '東京都港区南青山2-2-15'),
        array('電話番号', '050-3561-6058<br>※お電話による対応は行っておりません'),
        array('メールアドレス', $mail),
        array('お問い合わせ', 'お問い合わせは原則としてメールにて承っております。'),
        array('事業内容', '競馬情報サービスの運営、無料競馬診断、競馬予想情報の提供、デジタルコンテンツの販売'),
    ));
    $html .= '<h2>サービスについて</h2>';
    $html .= '<p>当サイトでは、競馬をより楽しんでいただくため、レース分析や無料診断などの情報サービスを提供しています。</p>';
    $html .= '<p>診断サービスは無料でご利用いただけます。また、LINE公式アカウントを通じて競馬関連情報や有料予想コンテンツをご案内する場合があります。</p>';
    $html .= '<p>提供する情報は、馬券の的中や利益を保証するものではありません。馬券の購入はご自身の判断と責任において行ってください。</p>';
    return $html;
}

/* ---------------------------------------------------------------- 特定商取引法に基づく表記 */

function keiba_legal_content_tokushoho()
{
    $mail = 'torunedojingmayuxiangxuanfeng@gmail.com';
    $html = keiba_legal_table(array(
        array('販売事業者', '笛田 千雄'),
        array('運営名称', 'プロによる競馬重賞無料予想運営事務局'),
        array('運営責任者', '笛田 千雄'),
        array('所在地', '東京都港区南青山2-2-15'),
        array('電話番号', '050-3561-6058'),
        array('メールアドレス', $mail),
        array('販売商品', 'ワンコイン競馬予想（note有料記事）'),
        array('販売価格', '1記事100円～500円（税込）'),
        array('商品代金以外の必要料金', 'インターネット接続に必要な通信料金等はお客様のご負担となります。'),
        array('支払方法', 'noteが提供する決済方法（クレジットカード、キャリア決済、PayPay、PayPal、Amazon Pay、noteポイント等）。利用可能な決済方法は購入画面に表示されます。'),
        array('支払時期・期限', 'note上での購入手続き時に決済が確定します。実際の引落時期は各決済サービスの規定に従います。'),
        array('商品の提供時期', 'noteでの決済完了後、原則として直ちに有料記事を閲覧できます。'),
        array('商品の提供方法', 'noteの有料記事としてオンラインで提供します。'),
        array('申込期間', '各記事の販売期間内。販売終了日時が設定されている場合は、記事内に表示します。'),
    ));

    $html .= '<h2>キャンセル・返品・返金について</h2>';
    $html .= '<p>本商品はデジタルコンテンツのため、購入後のお客様都合による返品・キャンセルは、法令上認められる場合を除き、原則としてお受けしておりません。</p>';
    $html .= '<p>ただし、note上で返金申請が可能な設定となっている記事については、note所定の期間および条件に従って返金申請ができます。</p>';
    $html .= '<p>当方の事情により、購入された予想情報を提供できなかった場合は、振替提供または返金等の対応を行います。</p>';
    $html .= '<p>対象レースが開催中止または不成立となった場合は、原則として後日開催される別レースの予想情報を振替提供いたします。</p>';
    $html .= '<p>振替提供が困難な場合や法令上返金が必要となる場合は、適切に対応いたします。</p>';

    $html .= '<h2>無料診断サービスについて</h2>';
    $html .= '<p>当サイトで提供する「今日の狙い目レース診断」は完全無料です。</p>';
    $html .= '<p>診断の利用およびLINE公式アカウントへの登録のみで料金が発生することはありません。</p>';
    $html .= '<p>有料商品「ワンコイン予想」は、note上で利用者が別途購入手続きを行った場合にのみ料金が発生します。</p>';

    $html .= '<h2>競馬予想に関する注意事項</h2>';
    $html .= keiba_legal_list(array(
        '当サービスが提供する競馬予想情報は、的中や利益を保証するものではありません。',
        '過去の的中実績は将来の結果を保証するものではありません。',
        '馬券の購入には損失が発生する可能性があります。購入は利用者ご自身の判断と責任において行ってください。',
        '20歳未満の方は馬券を購入できません。',
    ));

    $html .= '<h2>お問い合わせ</h2>';
    $html .= '<p>運営名称：プロによる競馬重賞無料予想運営事務局<br>メールアドレス：' . $mail . '<br>お問い合わせは原則としてメールにて承っております。</p>';
    return $html;
}

/* ---------------------------------------------------------------- プライバシーポリシー */

function keiba_legal_content_privacy()
{
    $mail = 'torunedojingmayuxiangxuanfeng@gmail.com';
    $html = '<p>プロによる競馬重賞無料予想運営事務局（運営責任者：笛田 千雄、以下「当事務局」といいます）は、「今日の狙い目レース診断」（以下「本サービス」といいます）において取得する個人情報等について、以下のとおりプライバシーポリシーを定めます。</p>';

    $html .= '<h2>第1条 取得する情報</h2>';
    $html .= '<p>当事務局は、本サービスの提供にあたり、以下の情報を取得する場合があります。</p>';
    $html .= keiba_legal_list(array(
        '氏名、メールアドレス、お問い合わせ内容',
        'LINE公式アカウントを通じて取得するプロフィール情報およびメッセージ内容',
        '診断時の回答内容、診断結果、利用履歴',
        'IPアドレス、Cookie、端末情報、アクセス日時',
        '有料サービスの購入履歴、決済関連情報',
    ));

    $html .= '<h2>第2条 利用目的</h2>';
    $html .= '<p>取得した情報は、以下の目的で利用します。</p>';
    $html .= keiba_legal_list(array(
        '無料診断サービスの提供',
        '診断結果の表示および関連情報の提供',
        'LINE公式アカウントを通じた競馬情報の配信',
        '有料予想サービスやキャンペーンのご案内',
        'お問い合わせへの対応',
        'サービスの改善および利用状況の分析',
        '広告配信および広告効果の測定',
        '不正利用の防止および安全管理',
    ));

    $html .= '<h2>第3条 個人情報の第三者提供</h2>';
    $html .= '<p>当事務局は、法令に基づく場合、本人の同意がある場合、その他個人情報保護法で認められる場合を除き、個人データを第三者に提供しません。</p>';
    $html .= '<p>サービスの運営上必要な範囲で、外部事業者へ個人情報の取扱いを委託する場合があります。</p>';

    $html .= '<h2>第4条 Cookieおよびアクセス解析</h2>';
    $html .= '<p>本サービスでは、利用状況の分析や広告効果の測定を目的として、Cookie等の技術を利用する場合があります。</p>';
    $html .= '<p>Google Analytics等のアクセス解析サービスや広告配信サービスを使用する場合、各サービスの提供事業者が定めるポリシーに従って情報が取り扱われます。</p>';
    $html .= '<p>本サービスで利用している解析サービス等と、外部へ送信される情報は、次のとおりです。</p>';
    $html .= keiba_legal_table(array(
        array(
            'Google Analytics 4<br>（提供：Google LLC）',
            '<strong>目的：</strong>アクセス状況の分析、診断の利用状況（診断の開始・完了、ボタンのクリックなど）と流入元の把握。<br>'
            . '<strong>送信される情報：</strong>閲覧したページ、閲覧日時、参照元（流入元）、端末・ブラウザの種類、おおよその地域、診断の操作に関する情報、IPアドレス、Cookie等の識別子。<br>'
            . '<strong>拒否（オプトアウト）の方法：</strong><a href="https://tools.google.com/dlpage/gaoptout" target="_blank" rel="noopener">Google アナリティクス オプトアウト アドオン</a>の利用、またはブラウザのCookie設定の変更。<br>'
            . '<strong>詳細：</strong><a href="https://policies.google.com/technologies/partner-sites" target="_blank" rel="noopener">Googleのサービスを使用するサイトやアプリから収集した情報のGoogleによる使用</a>',
        ),
        array(
            '当サイト内の利用状況の集計',
            '診断の開始数・完了数・ボタンのクリック数・流入元の種類を、個人を特定しない件数として集計しています。',
        ),
        array(
            '診断ページでの情報の保存',
            '診断の回答は、診断結果を表示するために当サイトのサーバーへ送信されますが、回答内容を個人と結び付けて保存することはありません。<br>'
            . '診断結果の再表示のため、回答の内容を、お使いのブラウザ内（ローカルストレージ）に最大24時間保存します。<br>'
            . 'LINE公式アカウントの案内から診断結果の詳細を表示した場合は、閲覧権限を確認するためのCookieを最大30日間保存します。',
        ),
        array(
            'LINE公式アカウント<br>（提供：LINEヤフー株式会社）',
            '友だち追加のボタンから、LINEのサービスへ移動します。LINEにおける情報の取扱いは、LINEヤフー株式会社のプライバシーポリシー等に従います。',
        ),
        array(
            '広告配信サービス',
            '広告を配信する場合は、広告の配信および効果測定のため、利用する広告サービスの提供事業者が、Cookie等により閲覧情報を取得することがあります。'
            . '利用する広告サービスが決まりましたら、サービス名と送信される情報を、本ポリシーに追記します。',
        ),
    ));
    $html .= '<p>ブラウザの設定でCookieを無効にすることで、Cookieによる情報の取得を拒否できます。ただし、一部の機能が利用できなくなる場合があります。</p>';

    $html .= '<h2>第5条 個人情報の安全管理</h2>';
    $html .= '<p>当事務局は、取得した個人情報について、不正アクセス、漏えい、紛失、改ざん等を防止するため、必要かつ適切な安全管理措置を講じます。</p>';

    $html .= '<h2>第6条 開示・訂正・利用停止等</h2>';
    $html .= '<p>利用者から個人情報の開示、訂正、利用停止、削除等について法令に基づく請求があった場合、本人確認のうえ、法令に従って対応します。</p>';

    $html .= '<h2>第7条 LINE公式アカウント</h2>';
    $html .= '<p>LINE公式アカウントでは、競馬関連情報、無料診断に関する情報、有料サービス等のご案内を配信する場合があります。</p>';
    $html .= '<p>配信を希望されない場合は、LINEアプリのブロック機能等をご利用いただけます。</p>';
    $html .= '<p>なお、ブロック操作のみでは、当事務局が保有する個人情報の削除が完了するものではありません。</p>';

    $html .= '<h2>第8条 ポリシーの変更</h2>';
    $html .= '<p>当事務局は、法令の改正やサービス内容の変更等に応じて、本ポリシーを変更する場合があります。</p>';
    $html .= '<p>変更後の内容は、本サービス内で公表します。</p>';

    $html .= '<h2>第9条 お問い合わせ窓口</h2>';
    $html .= '<p>運営名称：プロによる競馬重賞無料予想運営事務局<br>運営責任者：笛田 千雄<br>所在地：東京都港区南青山2-2-15<br>メールアドレス：' . $mail . '<br>制定日：2026年11月17日</p>';
    return $html;
}

/* ---------------------------------------------------------------- 利用規約 */

function keiba_legal_content_terms()
{
    $mail = 'torunedojingmayuxiangxuanfeng@gmail.com';
    $html = '<p>本利用規約（以下「本規約」といいます）は、プロによる競馬重賞無料予想運営事務局（運営責任者：笛田 千雄、以下「当事務局」といいます）が提供する「今日の狙い目レース診断」（以下「本サービス」といいます）の利用条件を定めるものです。</p>';
    $html .= '<p>利用者は、本規約をご確認のうえ、本サービスをご利用ください。</p>';

    $html .= '<h2>第1条 サービス内容</h2>';
    $html .= '<p>本サービスは、競馬に関する診断結果、分析情報、その他競馬関連情報を提供する情報サービスです。</p>';
    $html .= '<p>診断結果は、利用者の回答や当事務局が設定する分析基準等に基づいて提供されます。</p>';
    $html .= '<p>本サービスは、馬券の的中や利益を保証するものではありません。</p>';

    $html .= '<h2>第2条 利用料金</h2>';
    $html .= '<p>本サービスの診断機能は無料で利用できます。</p>';
    $html .= '<p>LINE公式アカウントへの登録のみで料金が発生することはありません。</p>';
    $html .= '<p>別途提供する有料競馬予想コンテンツについては、購入前に商品内容、価格、提供条件等を明示します。</p>';

    $html .= '<h2>第3条 LINE公式アカウント</h2>';
    $html .= '<p>本サービスでは、LINE公式アカウントを通じて競馬関連情報、診断結果に関する情報、キャンペーン情報等を提供する場合があります。</p>';
    $html .= '<p>配信内容には、有料サービスの案内が含まれる場合があります。</p>';

    $html .= '<h2>第4条 禁止事項</h2>';
    $html .= '<p>利用者は、以下の行為を行ってはなりません。</p>';
    $html .= keiba_legal_list(array(
        '法令または公序良俗に違反する行為',
        '本サービスの運営を妨害する行為',
        '不正アクセス等の行為',
        '提供情報の無断転載、複製、再販売',
        '第三者の権利を侵害する行為',
        'その他、当事務局が合理的な理由により不適切と判断する行為',
    ));

    $html .= '<h2>第5条 競馬予想に関する注意事項</h2>';
    $html .= '<p>本サービスで提供する情報は、競馬を楽しむための参考情報です。</p>';
    $html .= '<p>予想の的中、利益、収益の発生を保証するものではありません。</p>';
    $html .= '<p>過去の実績や分析結果は、将来の結果を保証するものではありません。</p>';
    $html .= '<p>馬券の購入には損失が発生する可能性があります。</p>';
    $html .= '<p>馬券の購入および購入金額の決定は、利用者ご自身の判断と責任において行ってください。</p>';
    $html .= '<p>20歳未満の方は馬券を購入できません。</p>';

    $html .= '<h2>第6条 有料コンテンツ</h2>';
    $html .= '<p>有料コンテンツの販売価格、支払方法、提供時期、キャンセル・返金条件等は、各販売ページおよび特定商取引法に基づく表記に定めます。</p>';
    $html .= '<p>本規約と個別の販売条件が異なる場合は、法令に反しない範囲で個別の販売条件を優先します。</p>';

    $html .= '<h2>第7条 知的財産権</h2>';
    $html .= '<p>本サービスに掲載する文章、画像、診断プログラム、分析情報等の知的財産権は、当事務局または正当な権利者に帰属します。</p>';
    $html .= '<p>法令上認められる場合を除き、無断転載、複製、再配布、販売等を禁止します。</p>';

    $html .= '<h2>第8条 サービスの変更・停止</h2>';
    $html .= '<p>当事務局は、システム保守、障害、天災その他やむを得ない事情により、本サービスの全部または一部を変更・停止する場合があります。</p>';
    $html .= '<p>有料コンテンツの提供に影響が生じた場合は、購入条件および法令に従って対応します。</p>';

    $html .= '<h2>第9条 免責事項</h2>';
    $html .= '<p>当事務局は、掲載情報の正確性や有用性の確保に努めますが、すべての情報の完全性や将来の結果を保証するものではありません。</p>';
    $html .= '<p>当事務局の責任については、消費者契約法その他の法令に従って取り扱います。</p>';
    $html .= '<p>本規約は、法令により認められる利用者の権利を制限するものではありません。</p>';

    $html .= '<h2>第10条 個人情報の取扱い</h2>';
    $html .= '<p>個人情報等の取扱いについては、別途定めるプライバシーポリシーに従います。</p>';

    $html .= '<h2>第11条 規約の変更</h2>';
    $html .= '<p>当事務局は、民法その他の法令に従い、本規約を変更する場合があります。</p>';
    $html .= '<p>変更する場合は、必要に応じて変更内容および効力発生日を本サービス内でお知らせします。</p>';

    $html .= '<h2>第12条 準拠法・管轄</h2>';
    $html .= '<p>本規約は日本法に準拠します。</p>';
    $html .= '<p>本サービスに関する紛争については、法令上認められる管轄裁判所において解決するものとします。</p>';

    $html .= '<h2>第13条 お問い合わせ</h2>';
    $html .= '<p>運営名称：プロによる競馬重賞無料予想運営事務局<br>運営責任者：笛田 千雄<br>メールアドレス：' . $mail . '<br>制定日：2026年11月17日</p>';
    return $html;
}

/** 設定 → Keiba AI Digest に、4つのページの場所と編集リンクを出す。 */
function keiba_legal_render_admin()
{
    $ids = (array) get_option('keiba_ai_digest_legal_page_ids', array());
    echo '<h2>会社概要・特商法・プライバシーポリシー・利用規約</h2>';
    echo '<p>お客様から提供された文面で、固定ページとして自動作成しています（既にあるページは上書きしません）。内容の変更は、各ページの編集画面で行ってください。診断ページの下にも、4つのページへのリンクを表示しています。</p>';
    echo '<ul>';
    foreach (keiba_legal_pages() as $key => $def) {
        $id = isset($ids[$key]) ? (int) $ids[$key] : 0;
        if ($id && get_post_status($id) && get_post_status($id) !== 'trash') {
            echo '<li>' . esc_html($def[1]) . '： <a href="' . esc_url(get_permalink($id)) . '" target="_blank" rel="noopener">' . esc_html(get_permalink($id)) . '</a>'
                . ' （<a href="' . esc_url(get_edit_post_link($id)) . '">編集</a>）</li>';
        } else {
            echo '<li>' . esc_html($def[1]) . '： 未作成（プラグインを有効化し直すか、固定ページを手動で作成してください）</li>';
        }
    }
    echo '</ul>';
}
